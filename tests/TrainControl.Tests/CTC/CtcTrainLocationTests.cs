using CTC.Core.Exceptions;
using CTC.Core.Models;
using CTC.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.CTC;

/// <summary>
/// Sequential-occupancy train tracking: CTC infers a dispatched train's current block from
/// anonymous <see cref="BlockStatusMessage"/> occupancy along its active route
/// (<see cref="CTCService.ApplyBlockStatus"/> and <c>TrainLocationTracker</c>).
/// </summary>
[TestClass]
public class CtcTrainLocationTests
{
    private static TimeSpan At(int hours, int minutes, int seconds) => new TimeSpan(hours, minutes, seconds);

    /// <summary>Releases every train at 12:00:00 on the Blue Line.</summary>
    private static async Task<(CTCService Service, FakeMessageSender Sender, List<CtcStateChangedEventArgs> Changes)> DispatchOnLayoutAsync(
        TrackLayoutMessage? layout = null,
        params ScheduledTrain[] trains)
    {
        var sender = new FakeMessageSender();
        var service = new CTCService(sender);
        service.ApplyTrackLayout(layout ?? BlueLine.CreateLayout());

        service.QueueSchedule(trains.Length > 0 ? trains : [BlueLine.Train("000", At(12, 0, 0))]);
        await service.SetSystemTimeAsync(At(12, 0, 0));
        Assert.HasCount(trains.Length > 0 ? trains.Length : 1, service.State.DispatchedTrains, "Setup: every train should have been dispatched.");

        var changes = new List<CtcStateChangedEventArgs>();
        service.StateChanged += (_, e) => changes.Add(e);
        return (service, sender, changes);
    }

    private static Task<(CTCService Service, FakeMessageSender Sender, List<CtcStateChangedEventArgs> Changes)> DispatchAsync(params ScheduledTrain[] trains) =>
        DispatchOnLayoutAsync(null, trains);

    private static void Report(CTCService service, string blockId, OccupancyState occupancy) =>
        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = blockId, Occupancy = occupancy });

    private static void Occupy(CTCService service, params string[] blockIds)
    {
        foreach (var blockId in blockIds)
        {
            Report(service, blockId, OccupancyState.Occupied);
        }
    }

    private static void Clear(CTCService service, params string[] blockIds)
    {
        foreach (var blockId in blockIds)
        {
            Report(service, blockId, OccupancyState.Clear);
        }
    }

    private static DispatchedTrainState Train(CTCService service, string trainId = "000") => service.State.FindDispatchedTrain(trainId)!;

    private static string Current(CTCService service, string trainId = "000") => Train(service, trainId).CurrentBlockId;

    private static List<CtcStateChangedEventArgs> LocationUpdates(List<CtcStateChangedEventArgs> changes) =>
        changes.Where(e => e.Kind == CtcStateChangeKind.TrainLocationUpdated).ToList();

    // ---- Dispatch ----

    [TestMethod]
    public async Task Dispatch_InitializesCurrentBlockAndActiveRoute()
    {
        var (service, _, _) = await DispatchAsync();

        var train = Train(service);
        Assert.AreEqual("A1", train.CurrentBlockId);
        CollectionAssert.AreEqual(BlueLine.BranchB, train.ActiveRouteBlockIds.ToArray());
        Assert.AreEqual(0, train.ActiveRouteIndex);
        Assert.IsFalse(train.CurrentBlockOccupancyObserved);
    }

    // ---- Single-train progression ----

    [TestMethod]
    public async Task StartBlockOccupied_DoesNotAdvance()
    {
        var (service, _, changes) = await DispatchAsync();

        Occupy(service, "A1");

        Assert.AreEqual("A1", Current(service));
        Assert.AreEqual(0, Train(service).ActiveRouteIndex);
        Assert.IsTrue(Train(service).CurrentBlockOccupancyObserved);
        Assert.IsEmpty(LocationUpdates(changes));
    }

    [TestMethod]
    public async Task NextRouteBlockOccupied_AdvancesAndRaisesLocationUpdate()
    {
        var (service, _, changes) = await DispatchAsync();
        Occupy(service, "A1");
        changes.Clear();

        Occupy(service, "A2");

        Assert.AreEqual("A2", Current(service));
        Assert.AreEqual(1, Train(service).ActiveRouteIndex);

        // The block's own status change first, then the train's location.
        Assert.HasCount(2, changes);
        Assert.AreEqual(CtcStateChangeKind.BlockStatus, changes[0].Kind);
        Assert.AreEqual(CtcStateChangeKind.TrainLocationUpdated, changes[1].Kind);
        Assert.AreEqual("000", changes[1].TrainId);
        Assert.AreEqual("A2", changes[1].BlockId);
    }

    [TestMethod]
    public async Task NextBlockOccupiedBeforeStartBlockSeen_DoesNotAdvance()
    {
        // The chain is established only once CTC has seen the train's current block occupied.
        var (service, _, changes) = await DispatchAsync();

        Occupy(service, "A2");

        Assert.AreEqual("A1", Current(service));
        Assert.IsEmpty(LocationUpdates(changes));
    }

    [TestMethod]
    public async Task NewBlockOccupiedBeforeOldClears_Advances()
    {
        var (service, _, _) = await DispatchAsync();

        Occupy(service, "A1", "A2");

        Assert.AreEqual(OccupancyState.Occupied, service.State.FindBlock("A1")!.Occupancy);
        Assert.AreEqual("A2", Current(service));
    }

    [TestMethod]
    public async Task TrailingBlockClears_DoesNotMoveTrainBackOrUnknown()
    {
        var (service, _, changes) = await DispatchAsync();
        Occupy(service, "A1", "A2");
        changes.Clear();

        Clear(service, "A1");

        Assert.AreEqual("A2", Current(service));
        Assert.AreEqual(1, Train(service).ActiveRouteIndex);
        Assert.IsEmpty(LocationUpdates(changes));
    }

    [TestMethod]
    public async Task OldBlockClearsBeforeNextOccupied_IsTolerated()
    {
        var (service, _, _) = await DispatchAsync();

        Occupy(service, "A1");
        Clear(service, "A1");
        Assert.AreEqual("A1", Current(service), "Clearing the current block must not move or remove the train.");

        Occupy(service, "A2");

        Assert.AreEqual("A2", Current(service));
    }

    [TestMethod]
    public async Task RepeatedOccupiedSnapshots_AdvanceOnlyOnce()
    {
        var (service, _, changes) = await DispatchAsync();
        Occupy(service, "A1", "A1");
        changes.Clear();

        Occupy(service, "A2", "A2", "A2");

        Assert.AreEqual("A2", Current(service));
        Assert.AreEqual(1, Train(service).ActiveRouteIndex);
        Assert.HasCount(1, LocationUpdates(changes));
    }

    [TestMethod]
    public async Task UnrelatedBlockOccupied_DoesNotMoveTrainOrThrow()
    {
        var (service, _, changes) = await DispatchAsync();
        Occupy(service, "A1");

        Occupy(service, "C13");

        Assert.AreEqual("A1", Current(service));
        Assert.AreEqual(OccupancyState.Occupied, service.State.FindBlock("C13")!.Occupancy);
        Assert.IsEmpty(LocationUpdates(changes));
    }

    [TestMethod]
    public async Task BlockFurtherAhead_IsNotSkippedTo()
    {
        var (service, _, changes) = await DispatchAsync();
        Occupy(service, "A1");

        Occupy(service, "A3");

        Assert.AreEqual("A1", Current(service));
        Assert.IsEmpty(LocationUpdates(changes));
    }

    [TestMethod]
    public async Task UnknownOccupancy_DoesNotAdvance()
    {
        var (service, _, changes) = await DispatchAsync();
        Occupy(service, "A1");

        Report(service, "A2", OccupancyState.Unknown);

        Assert.AreEqual("A1", Current(service));
        Assert.IsEmpty(LocationUpdates(changes));
    }

    [TestMethod]
    public async Task FullSequence_FollowsTheTrainAndNeverLosesIt()
    {
        var (service, _, changes) = await DispatchAsync();
        var seen = new List<string>();
        service.StateChanged += (_, _) => seen.Add(Current(service));

        Occupy(service, "A1");
        Occupy(service, "A2");
        Clear(service, "A1");
        Occupy(service, "A3");
        Clear(service, "A2");

        Assert.AreEqual("A3", Current(service));
        Assert.AreEqual(2, Train(service).ActiveRouteIndex);
        CollectionAssert.AreEqual(new[] { "A2", "A3" }, LocationUpdates(changes).Select(e => e.BlockId).ToArray());
        Assert.IsFalse(seen.Any(string.IsNullOrEmpty), "The train's current block must never become unknown.");
    }

    [TestMethod]
    public async Task InferenceDoesNotDependOnSystemTime()
    {
        // Far later than the schedule (and the clock moving backwards) changes nothing.
        var (service, _, _) = await DispatchAsync();
        await service.SetSystemTimeAsync(At(18, 0, 0));
        Occupy(service, "A1", "A2");
        await service.SetSystemTimeAsync(At(6, 0, 0));
        Occupy(service, "A3");

        Assert.AreEqual("A3", Current(service));
    }

    // ---- Several trains ----

    [TestMethod]
    public async Task TwoTrainsOnDifferentBranches_TrackedIndependently()
    {
        var (service, _, _) = await DispatchAsync(
            BlueLine.Train("000", At(12, 0, 0), route: BlueLine.BranchB),
            BlueLine.Train("001", At(12, 0, 0), route: ["C11", "C12", "C13", "C14", "C15"]));

        Occupy(service, "A1", "C11", "A2");
        Assert.AreEqual("A2", Current(service, "000"));
        Assert.AreEqual("C11", Current(service, "001"));

        Occupy(service, "C12");
        Clear(service, "A1", "C11");
        Occupy(service, "A3");

        Assert.AreEqual("A3", Current(service, "000"));
        Assert.AreEqual("C12", Current(service, "001"));
    }

    [TestMethod]
    public async Task OccupancyMatchingTwoTrainsNextBlock_IsAmbiguousAndMovesNeither()
    {
        // Both trains' next block is A5 (one from A4, the other from B6).
        var (service, _, changes) = await DispatchAsync(
            BlueLine.Train("000", At(12, 0, 0), route: ["A4", "A5", "C11", "C12"]),
            BlueLine.Train("001", At(12, 0, 0), route: ["B6", "A5", "C11", "C12"]));
        Occupy(service, "A4", "B6");
        changes.Clear();

        Occupy(service, "A5");

        Assert.AreEqual("A4", Current(service, "000"));
        Assert.AreEqual("B6", Current(service, "001"));
        Assert.AreEqual(OccupancyState.Occupied, service.State.FindBlock("A5")!.Occupancy);
        Assert.IsEmpty(LocationUpdates(changes));

        var ambiguous = changes.Single(e => e.Kind == CtcStateChangeKind.TrainLocationAmbiguous);
        Assert.AreEqual("A5", ambiguous.BlockId);
        Assert.AreEqual("Block A5 became occupied, but it could be Train 000 or Train 001; no train location was changed.", ambiguous.Message);
    }

    [TestMethod]
    public async Task OccupancyOfAnotherTrainsUnseenStartBlock_IsAmbiguous()
    {
        // Train 001 was released at A2 but not yet seen there; A2 becoming occupied could be
        // Train 001 appearing or Train 000 moving forward from A1.
        var (service, _, changes) = await DispatchAsync(
            BlueLine.Train("000", At(12, 0, 0), route: BlueLine.BranchB),
            BlueLine.Train("001", At(12, 0, 0), route: ["A2", "A3", "A4", "A5"]));
        Occupy(service, "A1");

        Occupy(service, "A2");

        Assert.AreEqual("A1", Current(service, "000"));
        Assert.AreEqual("A2", Current(service, "001"));
        Assert.IsFalse(Train(service, "001").CurrentBlockOccupancyObserved);
        Assert.HasCount(1, changes.Where(e => e.Kind == CtcStateChangeKind.TrainLocationAmbiguous));
    }

    [TestMethod]
    public async Task OccupancyOnAnotherLine_CannotAdvanceTrain()
    {
        // G1 is on another line. A valid route never leaves its line, so the train's active
        // route is forced to point there to prove the line check alone stops the move.
        var layout = BlueLine.CreateLayout();
        layout.Lines.Add(new TrackLineDefinition
        {
            LineId = "GREEN",
            Name = "Green Line",
            Blocks = { new TrackBlockDefinition { BlockId = "G1", BlockNumber = 1, Section = "G", LengthMeters = 50, SpeedLimitKilometersPerHour = 50, ConnectedBlockIds = ["A1"] } },
        });
        var (service, _, changes) = await DispatchOnLayoutAsync(layout);
        Occupy(service, "A1");
        Train(service).ActiveRouteBlockIds[1] = "G1";

        Occupy(service, "G1");

        Assert.AreEqual("A1", Current(service));
        Assert.IsEmpty(LocationUpdates(changes));
    }

    // ---- Dispatcher re-plans ----

    [TestMethod]
    public async Task SuccessfulReroute_FollowsNewActiveRoute()
    {
        // Scheduled A1..A5 -> B branch; at A5 the dispatcher sends it to C13 instead.
        var (service, _, _) = await DispatchAsync();
        Occupy(service, "A1", "A2", "A3", "A4", "A5");
        Assert.AreEqual("A5", Current(service));

        await service.UpdateDispatchedTrainPlanAsync("000", "C13", At(12, 10, 0));

        var train = Train(service);
        Assert.AreEqual("A5", train.CurrentBlockId);
        CollectionAssert.AreEqual(new[] { "A5", "C11", "C12", "C13" }, train.ActiveRouteBlockIds.ToArray());
        Assert.AreEqual(0, train.ActiveRouteIndex);
        CollectionAssert.AreEqual(BlueLine.BranchB, service.State.ScheduledTrains.Single().Route.Select(block => block.BlockId).ToArray());

        // The old path no longer explains occupancy; the new one does.
        Occupy(service, "B6");
        Assert.AreEqual("A5", Current(service));

        Occupy(service, "C11");
        Assert.AreEqual("C11", Current(service));
        Assert.AreEqual(1, train.ActiveRouteIndex);
    }

    [TestMethod]
    public async Task Reroute_RoutesFromInferredCurrentBlock()
    {
        var (service, sender, _) = await DispatchAsync();
        Occupy(service, "A1", "A2", "A3");
        Clear(service, "A1", "A2");

        await service.UpdateDispatchedTrainPlanAsync("000", "A5", At(12, 10, 0));

        // A3 A4 A5: authority covers the three blocks from the current block (occupied by this train).
        Assert.AreEqual(150.0, sender.SentMessages.OfType<MovementSuggestionMessage>().Last().SuggestedAuthorityMeters);
        CollectionAssert.AreEqual(new[] { "A3", "A4", "A5" }, Train(service).ActiveRouteBlockIds.ToArray());
    }

    [TestMethod]
    public async Task FailedRerouteSend_KeepsOldActiveRoute()
    {
        var (service, sender, _) = await DispatchAsync();
        Occupy(service, "A1", "A2", "A3", "A4", "A5");
        sender.ExceptionToThrow = new MessageSendException("Track Controller is not connected.");

        await Assert.ThrowsExactlyAsync<MessageSendException>(
            () => service.UpdateDispatchedTrainPlanAsync("000", "C13", At(12, 10, 0)));

        var train = Train(service);
        CollectionAssert.AreEqual(BlueLine.BranchB, train.ActiveRouteBlockIds.ToArray());
        Assert.AreEqual(4, train.ActiveRouteIndex);

        Occupy(service, "C11");
        Assert.AreEqual("A5", Current(service));

        Occupy(service, "B6");
        Assert.AreEqual("B6", Current(service));
    }
}
