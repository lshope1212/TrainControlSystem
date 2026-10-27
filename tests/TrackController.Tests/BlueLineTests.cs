using TrainControl.Contracts.Messages;
using System.Text.Json;
using TrackController.Contracts.Messages;
using TrackController.Core.Models;
using TrackController.Core.Services;
using TrackController.Runtime;
using TrainControl.Contracts.Enums;

namespace TrackController.Tests;

/// <summary>Exercises the course Blue Line (15 blocks, switch 5, crossing 3, signals 6 and 11) through the shared samples.</summary>
[TestClass]
public sealed class BlueLineTests
{
    private const double BlockMeters = 50;
    private const double SpeedLimit = 50 / 3.6;

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

        /// <summary>Returns the controlled test time for deterministic watchdog checks.</summary>
        public override DateTimeOffset GetUtcNow() => Now;

        /// <summary>Moves the test clock without delaying the test process.</summary>
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    private sealed class Fixture
    {
        public Clock Clock { get; } = new();

        public TrackControllerService Service { get; }

        /// <summary>Starts a controller on the single-territory Blue Line, with a train on block 1 and fresh feedback.</summary>
        public Fixture(bool plc = false)
        {
            Service = new(ReadDeployment("blue-line-all-pc.deployment.json").Territories.Single().Configuration, Clock);
            if (plc)
            {
                Service.LoadPlc("wayside.plc", File.ReadAllText(SamplePath("wayside.plc")));
            }
            else
            {
                Service.SetHardwareMode(false);
            }

            Feedback();
            Service.SetSimulation(false, 1);
        }

        /// <summary>Supplies fresh measurements: train on block 1, switch normal, crossing closed, proceed signals.</summary>
        public void Feedback(Action<List<TrackModelBlockStateMessage>>? change = null)
        {
            Clock.Advance(0.01);
            var rows = Service.GetConfiguration().Blocks.Select(blockDefinition => new TrackModelBlockStateMessage { BlockId = blockDefinition.BlockId, ObservedAtUtc = Clock.Now, Occupancy = blockDefinition.BlockId == "1" ? OccupancyState.Occupied : OccupancyState.Clear, Signal = SignalState.Green, Switch = SwitchPosition.Normal, Crossing = CrossingState.Closed, IsReservedByCtc = false }).ToList();
            change?.Invoke(rows);
            Service.ApplyFeedback(rows);
        }

        /// <summary>Requests movement from the yard-side block through the supplied route.</summary>
        public WaysideBlockStatus Submit(string target, string[] path, double authorityMeters = 450, double speed = 20)
        {
            Service.SubmitMovement(new()
            {
                RequestId = Guid.NewGuid().ToString("N"),
                TrainId = "Blue 1",
                TargetBlockId = target,
                PathBlockIds = [.. path],
                SuggestedSpeedMetersPerSecond = speed,
                SuggestedAuthorityMeters = authorityMeters
            });
            return Service.GetSnapshot().Authorizations.Last();
        }

        public WaysideBlockStatus Block(string id) => Service.GetSnapshot().Blocks.Single(blockStatus => blockStatus.BlockId == id);
    }

    private static readonly string[] ToStationB = ["2", "3", "4", "5", "6", "7", "8", "9", "10"];
    private static readonly string[] ToStationC = ["2", "3", "4", "5", "11", "12", "13", "14", "15"];

    private static string SamplePath(string name) => Path.Combine(AppContext.BaseDirectory, "PiSamples", name);

    private static PreparedDeployment ReadDeployment(string name) => WaysideDeployment.Read(SamplePath(name));

    /// <summary>Verifies that the Blue Line map matches the course workbook layout.</summary>
    [TestMethod]
    public void Read_BlueLineMap_MatchesCourseWorkbook()
    {
        var configuration = ReadDeployment("blue-line-all-pc.deployment.json").Territories.Single().Configuration;
        Assert.AreEqual("Blue Line", configuration.LineName);
        Assert.HasCount(15, configuration.Blocks);
        Assert.IsTrue(configuration.Blocks.All(block => block.LengthMeters == BlockMeters && Math.Abs(block.SpeedLimitMetersPerSecond - SpeedLimit) < 1e-9));
        CollectionAssert.AreEqual(new[] { "6", "11" }, configuration.Blocks.Where(block => block.HasSignal).Select(block => block.BlockId).ToArray());
        CollectionAssert.AreEqual(new[] { "3" }, configuration.Blocks.Where(block => block.HasCrossing).Select(block => block.BlockId).ToArray());
        var junction = configuration.Blocks.Single(block => block.NormalNextBlockId is not null);
        Assert.AreEqual("5", junction.BlockId);
        Assert.AreEqual("4", junction.StemBlockId);
        Assert.AreEqual("6", junction.NormalNextBlockId);
        Assert.AreEqual("11", junction.ReverseNextBlockId);
        CollectionAssert.AreEquivalent(new[] { "10", "15" }, configuration.Blocks.Where(block => block.NextBlockIds.Count == 0).Select(block => block.BlockId).ToArray());
        Assert.AreEqual("Station B", configuration.Blocks.Single(block => block.BlockId == "10").StationName);
        Assert.AreEqual("Station C", configuration.Blocks.Single(block => block.BlockId == "15").StationName);
        Assert.HasCount(2, configuration.Blocks.Where(block => block.StationName is not null).ToArray());
    }

    /// <summary>Verifies that station names reach the controller snapshot the map draws from.</summary>
    [TestMethod]
    public void GetSnapshot_StationBlocks_CarryStationNames()
    {
        var fixture = new Fixture();
        Assert.AreEqual("Station B", fixture.Block("10").StationName);
        Assert.AreEqual("Station C", fixture.Block("15").StationName);
        Assert.IsNull(fixture.Block("9").StationName);
    }

    /// <summary>Verifies that every Blue Line territory choice gives each block exactly one owner and keeps zones whole.</summary>
    [TestMethod]
    public void Read_BlueLineDeployments_AssignEveryBlockToOneOwner()
    {
        var all = Enumerable.Range(1, 15).Select(index => index.ToString()).Order().ToArray();
        foreach (string name in new[] { "blue-line-all-pc.deployment.json", "blue-line-pc-and-pi.deployment.json" })
        {
            var owners = ReadDeployment(name).Territories.SelectMany(territory => WaysideConfiguration.GetOwnedIds(territory.Configuration)).ToArray();
            CollectionAssert.AreEqual(all, owners.Order().ToArray(), name);
        }

        var split = ReadDeployment("blue-line-pc-and-pi.deployment.json");
        Assert.HasCount(2, split.Territories);
        Assert.IsTrue(split.Territories.Any(territory => WaysideConfiguration.GetOwnedIds(territory.Configuration).IsSupersetOf(["2", "3", "4", "5", "6", "11"])), "Switch 5 and crossing 3 zones share one territory.");
    }

    /// <summary>Verifies that missing, duplicate, unknown and zone-splitting ownership is rejected for the Blue Line.</summary>
    [TestMethod]
    public void Read_InvalidBlueLineOwnership_RejectsDeployment()
    {
        using var missing = new DeploymentFiles();
        missing.Edit("blue-line-territories.json", json => json["territories"]![1]!["blockIds"]!.AsArray().RemoveAt(10));
        Assert.Throws<ArgumentException>(() => missing.Read("blue-line-pc-and-pi.deployment.json"));

        using var duplicate = new DeploymentFiles();
        duplicate.Edit("blue-line-territories.json", json => json["territories"]![0]!["blockIds"]!.AsArray().Add("1"));
        Assert.Throws<ArgumentException>(() => duplicate.Read("blue-line-pc-and-pi.deployment.json"));

        using var unknown = new DeploymentFiles();
        unknown.Edit("blue-line-territories.json", json => json["territories"]![0]!["blockIds"]!.AsArray().Add("16"));
        Assert.Throws<ArgumentException>(() => unknown.Read("blue-line-pc-and-pi.deployment.json"));

        using var splitZone = new DeploymentFiles();
        splitZone.Edit("blue-line-territories.json", json =>
        {
            json["territories"]![1]!["blockIds"]!.AsArray().RemoveAt(5);
            json["territories"]![0]!["blockIds"]!.AsArray().Add("6");
        });
        Assert.Throws<ArgumentException>(() => splitZone.Read("blue-line-pc-and-pi.deployment.json"));
    }

    /// <summary>Verifies that the switch at 5 is refused while any block of its protection zone is occupied.</summary>
    [TestMethod]
    public void RequestSwitch_OccupiedProtectionZone_RejectsMovement()
    {
        foreach (string occupied in new[] { "4", "5", "6", "11" })
        {
            var fixture = new Fixture();
            fixture.Feedback(rows => rows.Single(feedback => feedback.BlockId == occupied).Occupancy = OccupancyState.Occupied);
            Assert.Throws<InvalidOperationException>(() => fixture.Service.RequestSwitch(new() { BlockId = "5", RequestedPosition = SwitchPosition.Reverse }), $"occupied {occupied}");
            Assert.AreEqual(SwitchPosition.Normal, fixture.Block("5").CommandedSwitch);
        }
    }

    /// <summary>Verifies that a clear zone lets the switch be commanded without fabricating position feedback.</summary>
    [TestMethod]
    public void RequestSwitch_ClearProtectionZone_CommandsReverseWithoutFeedback()
    {
        var fixture = new Fixture();
        fixture.Service.RequestSwitch(new() { BlockId = "5", RequestedPosition = SwitchPosition.Reverse });
        Assert.AreEqual(SwitchPosition.Reverse, fixture.Block("5").CommandedSwitch);
        Assert.AreEqual(SwitchPosition.Normal, fixture.Block("5").ReportedSwitch);
    }

    /// <summary>Verifies that crossing 3 closes for any train in its zone and reopens when the zone clears.</summary>
    [TestMethod]
    public void GetSnapshot_TrainApproachingCrossing_ClosesUntilZoneClears()
    {
        var fixture = new Fixture();
        Assert.AreEqual(CrossingState.Open, fixture.Block("3").CommandedCrossing);
        foreach (string occupied in new[] { "2", "3", "4" })
        {
            fixture.Feedback(rows => rows.Single(feedback => feedback.BlockId == occupied).Occupancy = OccupancyState.Occupied);
            Assert.AreEqual(CrossingState.Closed, fixture.Block("3").CommandedCrossing, $"occupied {occupied}");
        }

        fixture.Feedback(rows => rows.Single(feedback => feedback.BlockId == "1").Occupancy = OccupancyState.Clear);
        Assert.AreEqual(CrossingState.Open, fixture.Block("3").CommandedCrossing);
    }

    /// <summary>Verifies that a request through Station B on the normal branch is approved to the end of the line.</summary>
    [TestMethod]
    public void SubmitMovement_ToStationB_ApprovesNormalBranch()
    {
        var fixture = new Fixture();
        var authorization = fixture.Submit("1", ToStationB);
        Assert.AreEqual(450d, authorization.AuthorizedAuthorityMeters);
        Assert.AreEqual(SpeedLimit, authorization.AuthorizedSpeedMetersPerSecond, 1e-9);
        Assert.AreEqual("Restricted", authorization.MovementStatus);
        Assert.AreEqual(SignalState.Green, fixture.Block("6").CommandedSignal);
    }

    /// <summary>Verifies that a request through Station C needs the switch moved and reported reverse first.</summary>
    [TestMethod]
    public void SubmitMovement_ToStationC_RequiresReverseSwitchFeedback()
    {
        var fixture = new Fixture();
        StringAssert.Contains(fixture.Submit("1", ToStationC).MovementReason, "switch 5", StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual(0d, fixture.Service.GetSnapshot().Authorizations.Last().AuthorizedAuthorityMeters);
        fixture.Service.RequestSwitch(new() { BlockId = "5", RequestedPosition = SwitchPosition.Reverse });
        fixture.Feedback(rows => rows.Single(feedback => feedback.BlockId == "5").Switch = SwitchPosition.Reverse);
        var authorization = fixture.Service.GetSnapshot().Authorizations.Last();
        Assert.AreEqual(450d, authorization.AuthorizedAuthorityMeters);
        Assert.AreEqual(SignalState.Green, fixture.Block("11").CommandedSignal);
        Assert.AreEqual(SignalState.Red, fixture.Block("6").CommandedSignal);
    }

    /// <summary>Verifies that the normal-branch route is withheld while the switch points to Station C.</summary>
    [TestMethod]
    public void SubmitMovement_ToStationBWithReverseSwitch_WithholdsMovement()
    {
        var fixture = new Fixture();
        fixture.Service.RequestSwitch(new() { BlockId = "5", RequestedPosition = SwitchPosition.Reverse });
        fixture.Feedback(rows => rows.Single(feedback => feedback.BlockId == "5").Switch = SwitchPosition.Reverse);
        var authorization = fixture.Submit("1", ToStationB);
        Assert.AreEqual(0d, authorization.AuthorizedAuthorityMeters);
        StringAssert.Contains(authorization.MovementReason, "connects to 11");
    }

    /// <summary>Verifies that the outbound-only map offers no route from one branch back through the junction to the other.</summary>
    [TestMethod]
    public void SubmitMovement_BranchToBranch_IsNotRoutable()
    {
        var fixture = new Fixture();
        fixture.Feedback(rows =>
        {
            rows.Single(feedback => feedback.BlockId == "1").Occupancy = OccupancyState.Clear;
            rows.Single(feedback => feedback.BlockId == "6").Occupancy = OccupancyState.Occupied;
        });
        Assert.Throws<ArgumentException>(() => fixture.Submit("6", ["5", "11", "12"], 150));
    }

    /// <summary>Verifies that authority stops before an occupied block on the branch.</summary>
    [TestMethod]
    public void SubmitMovement_OccupiedBranchBlock_EndsAuthorityBeforeIt()
    {
        var fixture = new Fixture();
        fixture.Feedback(rows => rows.Single(feedback => feedback.BlockId == "8").Occupancy = OccupancyState.Occupied);
        var authorization = fixture.Submit("1", ToStationB);
        Assert.AreEqual(7 * BlockMeters, authorization.AuthorizedAuthorityMeters);
        StringAssert.Contains(authorization.MovementReason, "block 8");
    }

    /// <summary>Verifies that a route over crossing 3 waits for closed feedback.</summary>
    [TestMethod]
    public void SubmitMovement_CrossingNotConfirmedClosed_WithholdsMovement()
    {
        var fixture = new Fixture();
        fixture.Feedback(rows => rows.Single(feedback => feedback.BlockId == "3").Crossing = CrossingState.Open);
        var authorization = fixture.Submit("1", ToStationB);
        Assert.AreEqual(0d, authorization.AuthorizedAuthorityMeters);
        StringAssert.Contains(authorization.MovementReason, "crossing 3");
        fixture.Feedback();
        Assert.AreEqual(450d, fixture.Service.GetSnapshot().Authorizations.Last().AuthorizedAuthorityMeters);
    }

    /// <summary>Verifies that the unchanged shared PLC drives the Blue Line switch, signals and crossing.</summary>
    [TestMethod]
    public void SubmitMovement_SharedPlcProgram_ControlsBlueLine()
    {
        var movement = new Fixture(plc: true);
        Assert.AreEqual(450d, movement.Submit("1", ToStationB).AuthorizedAuthorityMeters);
        Assert.Throws<InvalidOperationException>(() => movement.Service.RequestSwitch(new() { BlockId = "5", RequestedPosition = SwitchPosition.Reverse }), "A switch inside active authority must not move.");

        var junction = new Fixture(plc: true);
        junction.Service.RequestSwitch(new() { BlockId = "5", RequestedPosition = SwitchPosition.Reverse });
        Assert.AreEqual(SwitchPosition.Reverse, junction.Block("5").CommandedSwitch);
        Assert.AreEqual(CrossingState.Open, junction.Block("3").CommandedCrossing);
        junction.Feedback(rows => rows.Single(feedback => feedback.BlockId == "2").Occupancy = OccupancyState.Occupied);
        Assert.AreEqual(CrossingState.Closed, junction.Block("3").CommandedCrossing);
    }

    /// <summary>Verifies that the controller map cannot drift from the Track Model's Blue Line when both are present.</summary>
    [TestMethod]
    public void BlueLineMap_TrackModelLayout_AgreesOnBlocksAndEquipment()
    {
        string? path = null;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null && path is null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "src", "TrackModel", "SampleLayouts", "blue-line.json");
            path = File.Exists(candidate) ? candidate : null;
        }

        if (path is null)
        {
            Assert.Inconclusive("The Track Model layout is not on this branch; merge main to enable the drift check.");
            return;
        }

        var controller = ReadDeployment("blue-line-all-pc.deployment.json").Territories.Single().Configuration.Blocks;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var model = document.RootElement.GetProperty("blocks").EnumerateArray().ToArray();
        Assert.HasCount(model.Length, controller);
        foreach (var (block, expected) in controller.Zip(model))
        {
            string id = expected.GetProperty("id").GetString()!;
            Assert.AreEqual(id, block.BlockId);
            Assert.AreEqual(expected.GetProperty("lengthMeters").GetDouble(), block.LengthMeters, id);
            Assert.AreEqual(expected.GetProperty("speedLimitMetersPerSecond").GetDouble(), block.SpeedLimitMetersPerSecond, 1e-9, id);
            Assert.AreEqual(expected.GetProperty("hasSignal").GetBoolean(), block.HasSignal, id);
            Assert.AreEqual(expected.GetProperty("hasCrossing").GetBoolean(), block.HasCrossing, id);
            Assert.AreEqual(expected.GetProperty("hasSwitch").GetBoolean(), block.NormalNextBlockId is not null, id);
            Assert.AreEqual(expected.GetProperty("stationName").GetString() ?? "", block.StationName ?? "", id);
            var connected = expected.GetProperty("connectedBlockIds").EnumerateArray().Select(element => element.GetString()!).ToArray();
            Assert.IsTrue(block.NextBlockIds.All(connected.Contains), $"Block {id} has an edge the Track Model lacks.");
            if (block.NormalNextBlockId is not null)
            {
                Assert.AreEqual(expected.GetProperty("normalNextBlockId").GetString(), block.NormalNextBlockId);
                Assert.AreEqual(expected.GetProperty("reverseNextBlockId").GetString(), block.ReverseNextBlockId);
            }
        }
    }
}
