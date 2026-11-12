using TrackController.Contracts.Messages;
using TrackController.Runtime;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CrossTerritoryTests
{
    private const string Project = "course-territories-all-pc.deployment.json";
    private static MovementSuggestionMessage Request(string id = "before", string target = "GREEN-109", string? predecessor = null) => new()
    {
        RequestId = id, TrainId = "T", TargetBlockId = target,
        EntryBlockId = target == "GREEN-109" ? "GREEN-108" : "GREEN-109",
        PathBlockIds = target == "GREEN-109" ? ["GREEN-110", "GREEN-111"] : ["GREEN-111"],
        SuggestedSpeedMetersPerSecond = 8, SuggestedAuthorityMeters = target == "GREEN-109" ? 300 : 200,
        PredecessorRequestId = predecessor
    };
    private static WaysideBlockStatus Block(TerritoryRuntimeRouter router, string id) => router.LatestSnapshot.Blocks.Single(block => block.BlockId == id);
    private static async Task Ready(TerritoryRuntimeRouter router, PreparedDeployment plan, string occupied = "GREEN-109")
    {
        foreach (var line in plan.Territories.GroupBy(territory => territory.Configuration.LineName))
            Assert.IsTrue((await PiRuntimeTests.Send(router, PiRuntimeTests.Clear(line.First().Configuration, occupied))).IsAccepted);
        Assert.IsTrue((await PiRuntimeTests.Send(router, MessageSerializer.CreateEnvelope("SetSimulation", new { paused = false, speedMultiplier = 1 }))).IsAccepted);
    }

    [TestMethod]
    public async Task IndependentLines_CanUseTheSameRequestLabelWithoutSharingAuthority()
    {
        using var files = new DeploymentFiles();
        var plan = files.Read(Project);
        await using var router = plan.CreateRuntime();
        await Ready(router, plan);
        await PiRuntimeTests.Send(router, PiRuntimeTests.Clear(plan.Territories.First(territory => territory.Configuration.LineName == "Red Line").Configuration, "RED-28"));
        await PiRuntimeTests.Send(router, Request());
        Assert.IsTrue((await PiRuntimeTests.Send(router, new MovementSuggestionMessage
        {
            RequestId = "before", TrainId = "R", TargetBlockId = "RED-28", EntryBlockId = "RED-27",
            PathBlockIds = ["RED-29", "RED-30"], SuggestedAuthorityMeters = 150, SuggestedSpeedMetersPerSecond = 8
        })).IsAccepted);
        Assert.AreEqual(300, Block(router, "GREEN-109").AuthorizedAuthorityMeters, 1e-6);
        Assert.AreEqual(150, Block(router, "RED-28").AuthorizedAuthorityMeters, 1e-6);
    }
    [TestMethod]
    public async Task InitiallyRestrictedJointGrant_DoesNotFalseStopOrExtendWhenFeedbackCatchesUp()
    {
        using var files = new DeploymentFiles();
        var plan = files.Read(Project);
        await using var router = plan.CreateRuntime();
        await Ready(router, plan);
        await PiRuntimeTests.Send(router, new MaintenanceRequestMessage { BlockId = "GREEN-111", RequestedState = MaintenanceState.Closed });
        await PiRuntimeTests.Send(router, Request());
        Assert.AreEqual(200, Block(router, "GREEN-109").AuthorizedAuthorityMeters, 1e-6);
        var restricted = Block(router, "GREEN-109");
        Assert.AreEqual("Restricted", restricted.MovementStatus);
        StringAssert.Contains(restricted.MovementReason, "Authority is shorter than the CTC request.");
        StringAssert.Contains(restricted.MovementReason, "Speed is lower than the CTC suggestion.");
        // Track Model later confirms the closure to every controller. The original 200 m remains safe.
        await PiRuntimeTests.Send(router, new TrackModelBlockStateMessage
        {
            BlockId = "GREEN-111", ObservedAtUtc = DateTimeOffset.UtcNow, Occupancy = OccupancyState.Clear, IsClosed = true
        });
        Assert.AreEqual(200, Block(router, "GREEN-109").AuthorizedAuthorityMeters, 1e-6);
        await PiRuntimeTests.Send(router, new MaintenanceRequestMessage { BlockId = "GREEN-111", RequestedState = MaintenanceState.Open });
        await Ready(router, plan);
        Assert.AreEqual(200, Block(router, "GREEN-109").AuthorizedAuthorityMeters, 1e-6, "Reopening cannot extend the issued endpoint.");
        StringAssert.Contains(Block(router, "GREEN-109").MovementReason, "Authority is held at the endpoint already issued for this block.");
        var held = Request();
        held.SuggestedSpeedMetersPerSecond = 0;
        await PiRuntimeTests.Send(router, held);
        Assert.AreEqual(0, Block(router, "GREEN-109").AuthorizedAuthorityMeters);
        await PiRuntimeTests.Send(router, Request());
        Assert.AreEqual(300, Block(router, "GREEN-109").AuthorizedAuthorityMeters, 1e-6, "CTC can authorize departure using its existing same-ID speed transition.");
        Assert.AreEqual("Speed is lower than the CTC suggestion.", Block(router, "GREEN-109").MovementReason);
        var fullyApproved = Request();
        fullyApproved.SuggestedSpeedMetersPerSecond = 7;
        await PiRuntimeTests.Send(router, fullyApproved);
        Assert.AreEqual("Approved", Block(router, "GREEN-109").MovementStatus);
        Assert.AreEqual("", Block(router, "GREEN-109").MovementReason);
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CrossBoundary_KeepsEndpointAndOneOwner_OnPcAndPi(bool usePi)
    {
        using var files = new DeploymentFiles();
        var plan = files.Read(Project);
        await using var server = new PiRuntimeServer(0);
        server.Start();
        if (usePi) plan = plan.WithExecution(plan.Territories.Select(territory => new TerritoryAssignment
        { TerritoryId = territory.Id, Execution = territory.Id == "wayside-green-a" ? "Pi" : "Local", Host = "127.0.0.1", Port = territory.Id == "wayside-green-a" ? server.Port : territory.Port }));
        await using var router = plan.CreateRuntime();
        await Ready(router, plan);
        Assert.AreNotEqual(router.GetOwnerDescription("GREEN-109"), router.GetOwnerDescription("GREEN-110"));
        Assert.IsTrue((await PiRuntimeTests.Send(router, Request())).IsAccepted);
        Assert.AreEqual(300, Block(router, "GREEN-109").AuthorizedAuthorityMeters, 1e-6);
        var staged = await PiRuntimeTests.Send(router, Request("after", "GREEN-110", "before"));
        Assert.IsTrue(staged.IsAccepted, staged.Detail);
        Assert.IsTrue(staged.IsStagedSuccessor);
        Assert.AreEqual(200, Block(router, "GREEN-110").Continuation!.AuthorizedAuthorityMeters, 1e-6);
        await Ready(router, plan, "GREEN-110");
        Assert.IsNull(Block(router, "GREEN-109").Request);
        Assert.AreEqual("after", Block(router, "GREEN-110").Request!.RequestId);
        Assert.AreEqual(200, Block(router, "GREEN-110").AuthorizedAuthorityMeters, 1e-6);
        Assert.HasCount(241, router.LatestSnapshot.Blocks.Select(block => block.BlockId).Distinct().ToArray());
        Assert.HasCount(1, router.LatestSnapshot.Authorizations);

        // An old renewal can arrive after the transition. Every Green wayside rejects it, but
        // the acknowledgement names the old target and reports that reason only once.
        var lateRenewal = await PiRuntimeTests.Send(router, Request());
        Assert.IsFalse(lateRenewal.IsAccepted);
        Assert.IsTrue(lateRenewal.IsRetiredMovement);
        Assert.AreEqual("Target block GREEN-109: This request's target block cleared. Submit a new request ID and current target block.", lateRenewal.Detail);
        Assert.AreEqual(200, Block(router, "GREEN-110").AuthorizedAuthorityMeters, 1e-6);
    }

    [TestMethod]
    public async Task RemoteClosure_WithdrawsWholeIssuedGrant_AndRestoresSameLimit()
    {
        using var files = new DeploymentFiles();
        var plan = files.Read(Project);
        await using var router = plan.CreateRuntime();
        await Ready(router, plan);
        await PiRuntimeTests.Send(router, Request());
        await PiRuntimeTests.Send(router, Request("after", "GREEN-110", "before"));
        await PiRuntimeTests.Send(router, new MaintenanceRequestMessage { BlockId = "GREEN-111", RequestedState = MaintenanceState.Closed });
        Assert.AreEqual(0, Block(router, "GREEN-109").AuthorizedAuthorityMeters);
        Assert.IsNull(Block(router, "GREEN-110").Continuation);
        await PiRuntimeTests.Send(router, new MaintenanceRequestMessage { BlockId = "GREEN-111", RequestedState = MaintenanceState.Open });
        Assert.AreEqual(300, Block(router, "GREEN-109").AuthorizedAuthorityMeters, 1e-6);
        Assert.AreEqual(200, Block(router, "GREEN-110").Continuation!.AuthorizedAuthorityMeters, 1e-6);
    }

    [TestMethod]
    public async Task CrossingAfterJointRevocation_CannotPromoteAnIndividualWaysidesGrant()
    {
        using var files = new DeploymentFiles();
        var plan = files.Read(Project);
        await using var router = plan.CreateRuntime();
        await Ready(router, plan);
        await PiRuntimeTests.Send(router, Request());
        await PiRuntimeTests.Send(router, Request("after", "GREEN-110", "before"));
        await PiRuntimeTests.Send(router, new MaintenanceRequestMessage { BlockId = "GREEN-111", RequestedState = MaintenanceState.Closed });
        await Ready(router, plan, "GREEN-110");
        Assert.AreEqual(0, Block(router, "GREEN-110").AuthorizedAuthorityMeters);
        await PiRuntimeTests.Send(router, new MaintenanceRequestMessage { BlockId = "GREEN-111", RequestedState = MaintenanceState.Open });
        Assert.AreEqual(0, Block(router, "GREEN-110").AuthorizedAuthorityMeters, "Reopening cannot retroactively authorize an unapproved crossing.");
    }

    [TestMethod]
    public async Task LostPi_RemovesAuthorityAndContinuationFromLocalApproach()
    {
        using var files = new DeploymentFiles();
        var plan = files.Read(Project);
        PiRuntimeServer? server = new PiRuntimeServer(0);
        server.Start();
        try
        {
            plan = plan.WithExecution(plan.Territories.Select(territory => new TerritoryAssignment
            { TerritoryId = territory.Id, Execution = territory.Id == "wayside-green-a" ? "Pi" : "Local", Host = "127.0.0.1", Port = territory.Id == "wayside-green-a" ? server.Port : territory.Port }));
            await using var router = plan.CreateRuntime();
            await Ready(router, plan);
            await PiRuntimeTests.Send(router, Request());
            await PiRuntimeTests.Send(router, Request("after", "GREEN-110", "before"));
            Assert.IsGreaterThan(0d, Block(router, "GREEN-109").AuthorizedAuthorityMeters);
            await server.DisposeAsync();
            server = null;
            await router.PollAsync();
            Assert.AreEqual(0, Block(router, "GREEN-109").AuthorizedAuthorityMeters);
            Assert.IsNull(Block(router, "GREEN-110").Continuation);
        }
        finally { if (server is not null) await server.DisposeAsync(); }
    }

    [TestMethod]
    public async Task ConflictingRequestFromOtherTerritory_IsWithheld_AndCancellationReleasesOriginal()
    {
        using var files = new DeploymentFiles();
        var plan = files.Read(Project);
        await using var router = plan.CreateRuntime();
        await Ready(router, plan);
        await PiRuntimeTests.Send(router, Request());
        var conflict = Request("opposing", "GREEN-110");
        await PiRuntimeTests.Send(router, conflict);
        Assert.IsTrue(router.LatestSnapshot.Authorizations.All(block => block.AuthorizedAuthorityMeters == 0));
        conflict.IsActive = false;
        await PiRuntimeTests.Send(router, conflict);
        Assert.AreEqual(300, Block(router, "GREEN-109").AuthorizedAuthorityMeters, 1e-6);
    }
}
