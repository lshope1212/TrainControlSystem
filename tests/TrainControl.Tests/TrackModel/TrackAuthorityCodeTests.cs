using TrackModel.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.TrackModel;

/// <summary>
/// The Track Model's per-block speed/authority codes as a train receives them: continuation codes
/// apply only across the boundary they were prepared for, wayside batches are applied in order, and a
/// code whose lease lapses becomes an authoritative zero.
/// </summary>
[TestClass]
public class TrackAuthorityCodeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static TrackService CreateService()
    {
        var service = new TrackService();
        service.LoadLayout(SampleTrackLayout.Create());
        return service;
    }

    private static TrackModelCommandMessage Code(string blockId, double authority, string requestId, string entry = "") => new()
    {
        BlockId = blockId,
        CommandedSpeedMetersPerSecond = authority > 0 ? 12 : 0,
        AuthorityMeters = authority,
        MovementRequestId = authority > 0 ? requestId : "",
        AuthorityEntryBlockId = entry,
        Switch = SwitchPosition.Normal,
        Signal = SignalState.Red,
        Crossing = CrossingState.Closed,
    };

    private static TrackModelCommandBatchMessage Batch(long sequence, double lease, params TrackModelCommandMessage[] commands) =>
        new() { Sequence = sequence, AuthorityLeaseSeconds = lease, Commands = [.. commands] };

    [TestMethod]
    public void ContinuationCode_ReachesOnlyATrainThatEnteredFromItsEntryBlock()
    {
        var service = CreateService();
        service.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "104" });
        service.ApplyCommandBatch(Batch(1, 0, Code("104", 300, "P"), Code("105", 178.08, "P", entry: "104")), Now);
        Assert.AreEqual(300d, service.CreateTrainEnvironment("104").AuthorityMeters);
        Assert.AreEqual("P", service.CreateTrainEnvironment("104").MovementRequestId);

        // The train crosses 104 -> 105: the code prepared for it there applies at once. No zero.
        service.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "105", ActualSpeedMetersPerSecond = 10 });
        var entered = service.CreateTrainEnvironment("105");
        Assert.AreEqual("001", entered.TrainId);
        Assert.AreEqual(178.08, entered.AuthorityMeters, 1e-9);
        Assert.AreEqual(12d, entered.CommandedSpeedMetersPerSecond);
        Assert.AreEqual("P", entered.MovementRequestId);
        Assert.AreEqual(178.08, service.CaptureTrainCodes()["001"].Authority, 1e-9);
    }

    [TestMethod]
    public void ContinuationCode_GivesAnyOtherOccupantZero()
    {
        // A train placed directly into the block.
        var placed = CreateService();
        placed.ApplyCommandBatch(Batch(1, 0, Code("105", 178.08, "P", entry: "104")), Now);
        placed.ApplyTrainUpdate(new() { TrainId = "002", CurrentBlockId = "105" });
        Assert.AreEqual(0d, placed.CreateTrainEnvironment("105").AuthorityMeters);
        Assert.AreEqual(0d, placed.CreateTrainEnvironment("105").CommandedSpeedMetersPerSecond);
        Assert.AreEqual("", placed.CreateTrainEnvironment("105").MovementRequestId);

        // A train entering from the other side.
        var reverse = CreateService();
        reverse.ApplyTrainUpdate(new() { TrainId = "002", CurrentBlockId = "106" });
        reverse.ApplyCommandBatch(Batch(1, 0, Code("105", 178.08, "P", entry: "104")), Now);
        reverse.ApplyTrainUpdate(new() { TrainId = "002", CurrentBlockId = "105" });
        Assert.AreEqual(0d, reverse.CreateTrainEnvironment("105").AuthorityMeters);

        // An unoccupied block reports its code as commanded (for the TestUI/diagnostics).
        Assert.AreEqual(178.08, CreateServiceWith(Code("105", 178.08, "P", entry: "104")).CreateTrainEnvironment("105").AuthorityMeters, 1e-9);
    }

    private static TrackService CreateServiceWith(params TrackModelCommandMessage[] commands)
    {
        var service = CreateService();
        service.ApplyCommandBatch(Batch(1, 0, commands), Now);
        return service;
    }

    [TestMethod]
    public void OlderOrRepeatedBatches_CannotRestoreWithdrawnAuthority()
    {
        var service = CreateService();
        service.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "104" });
        service.ApplyCommandBatch(Batch(10, 0, Code("104", 300, "P")), Now);
        service.ApplyCommandBatch(Batch(11, 0, Code("104", 0, "")), Now);

        // The older grant arrives late, and the newest is replayed: neither changes the stop code.
        Assert.IsEmpty(service.ApplyCommandBatch(Batch(10, 0, Code("104", 300, "P")), Now), "Skipped, not refused.");
        service.ApplyCommandBatch(Batch(11, 0, Code("104", 300, "P")), Now);
        Assert.AreEqual(0d, service.CreateTrainEnvironment("104").AuthorityMeters);

        // A newer batch applies; an unsequenced (TestUI) command still applies as before.
        service.ApplyCommandBatch(Batch(12, 0, Code("104", 250, "Q")), Now);
        Assert.AreEqual(250d, service.CreateTrainEnvironment("104").AuthorityMeters);
        service.ApplyCommand(Code("104", 0, ""));
        Assert.AreEqual(0d, service.CreateTrainEnvironment("104").AuthorityMeters);
    }

    [TestMethod]
    public void LapsedLease_TurnsTheCodeIntoAZero_UnlessANewerBatchRenewsIt()
    {
        var service = CreateService();
        service.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "104" });
        service.ApplyCommandBatch(Batch(1, 1.5, Code("104", 300, "P"), Code("105", 178.08, "P", entry: "104")), Now);

        Assert.AreEqual(0, service.ExpireCommands(Now.AddSeconds(1)));
        service.ApplyCommandBatch(Batch(2, 1.5, Code("104", 300, "P"), Code("105", 178.08, "P", entry: "104")), Now.AddSeconds(1));
        Assert.AreEqual(0, service.ExpireCommands(Now.AddSeconds(2)), "Renewed by the newer batch.");
        Assert.AreEqual(300d, service.CreateTrainEnvironment("104").AuthorityMeters);

        // The Track Controller goes silent: the codes lapse to zero.
        Assert.AreEqual(2, service.ExpireCommands(Now.AddSeconds(2.6)));
        Assert.AreEqual(0d, service.CreateTrainEnvironment("104").AuthorityMeters);
        Assert.AreEqual(0d, service.CreateTrainEnvironment("104").CommandedSpeedMetersPerSecond);
        Assert.AreEqual("", service.CreateTrainEnvironment("104").MovementRequestId);
        Assert.AreEqual(0d, service.FindBlock("105")!.AuthorityMeters);
    }

    [TestMethod]
    public void InvalidContinuationEntry_IsRefused()
    {
        var service = CreateService();
        var refused = service.ApplyCommandBatch(Batch(1, 0, Code("105", 100, "P", entry: "999"), Code("106", 100, "P", entry: "106")), Now);
        Assert.HasCount(2, refused);
        Assert.AreEqual(0d, service.FindBlock("105")!.AuthorityMeters);
    }
}
