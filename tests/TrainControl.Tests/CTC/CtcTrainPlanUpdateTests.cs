using CTC.Core.Dispatching;
using CTC.Core.Exceptions;
using CTC.Core.Models;
using CTC.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.CTC;

/// <summary>
/// Post-dispatch changes to a train's movement plan (destination and/or arrival time):
/// <see cref="CTCService.UpdateDispatchedTrainPlanAsync"/> and the routing/speed/authority
/// rules it uses.
/// </summary>
[TestClass]
public class CtcTrainPlanUpdateTests
{
    private const double MaxBlueSpeed = 50.0 / 3.6;

    private static TimeSpan At(int hours, int minutes, int seconds) => new TimeSpan(hours, minutes, seconds);

    /// <summary>
    /// Train 000 on branch B, released at 12:00:00 from A1. Its next timed block is A3 at
    /// 12:10:00 (100 m ahead), so that is its initial movement target.
    /// </summary>
    private static async Task<(CTCService Service, FakeMessageSender Sender)> DispatchTrainAsync(TrackLayoutMessage? layout = null)
    {
        var sender = new FakeMessageSender();
        var service = new CTCService(sender);
        service.ApplyTrackLayout(layout ?? BlueLine.CreateLayout());

        var train = new ScheduledTrain { TrainId = "000", LineId = BlueLine.LineId };
        foreach (var blockId in BlueLine.BranchB)
        {
            train.Route.Add(new ScheduledRouteBlock
            {
                BlockId = blockId,
                ArrivalTime = blockId switch
                {
                    "A1" => At(12, 0, 0),
                    "A3" => At(12, 10, 0),
                    "B10" => At(12, 30, 0),
                    _ => null,
                },
            });
        }

        service.QueueSchedule([train]);
        await service.SetSystemTimeAsync(At(12, 0, 0));

        Assert.HasCount(1, service.State.DispatchedTrains, "Setup: the train should have been dispatched.");
        return (service, sender);
    }

    private static List<MovementSuggestionMessage> Suggestions(FakeMessageSender sender) =>
        sender.SentMessages.OfType<MovementSuggestionMessage>().ToList();

    private static List<MovementRequestMessage> Requests(FakeMessageSender sender) =>
        sender.SentMessages.OfType<MovementRequestMessage>().ToList();

    // ---- Initial dispatch ----

    [TestMethod]
    public async Task Dispatch_StoresNextTimedBlockAsActiveTarget()
    {
        var (service, _) = await DispatchTrainAsync();

        var train = service.State.DispatchedTrains.Single();
        Assert.AreEqual("A3", train.TargetBlockId);
        Assert.AreEqual(At(12, 10, 0), train.TargetArrivalTime);
        Assert.AreEqual("A1", train.CurrentBlockId);
    }

    [TestMethod]
    public async Task Dispatch_InitialSuggestionUnchanged()
    {
        // 100 m to A3 in 600 s; authority covers the whole 10-block route.
        var (_, sender) = await DispatchTrainAsync();

        var suggestion = Suggestions(sender).Single();
        Assert.AreEqual(100.0 / 600.0, suggestion.SuggestedSpeedMetersPerSecond, 1e-9);
        Assert.AreEqual(500.0, suggestion.SuggestedAuthorityMeters);
        Assert.HasCount(1, Requests(sender));
    }

    // ---- Arrival-time change ----

    [TestMethod]
    public async Task LaterArrival_SameDestination_LowersSpeedAndSendsOnlyASuggestion()
    {
        var (service, sender) = await DispatchTrainAsync();
        double initialSpeed = service.State.DispatchedTrains.Single().SuggestedSpeedMetersPerSecond;

        string? warning = await service.UpdateDispatchedTrainPlanAsync("000", "A3", At(12, 15, 0));

        Assert.IsNull(warning);

        // Exactly one new message, a suggestion; no second release.
        Assert.HasCount(3, sender.SentMessages);
        var suggestion = (MovementSuggestionMessage)sender.SentMessages[2];
        Assert.HasCount(1, Requests(sender));

        // 100 m (A1, A2) in 900 s. Only the time changed, so the route (and the later stop
        // B10) is kept and the authority still covers the whole remaining A1..B10 route.
        Assert.AreEqual("000", suggestion.TrainId);
        Assert.AreEqual(100.0 / 900.0, suggestion.SuggestedSpeedMetersPerSecond, 1e-9);
        Assert.IsLessThan(initialSpeed, suggestion.SuggestedSpeedMetersPerSecond);
        Assert.AreEqual(500.0, suggestion.SuggestedAuthorityMeters);

        var train = service.State.DispatchedTrains.Single();
        Assert.AreEqual("A3", train.TargetBlockId);
        Assert.AreEqual(At(12, 15, 0), train.TargetArrivalTime);
        Assert.AreEqual(suggestion.SuggestedSpeedMetersPerSecond, train.SuggestedSpeedMetersPerSecond);
        Assert.AreEqual(suggestion.SuggestedAuthorityMeters, train.SuggestedAuthorityMeters);
        Assert.AreEqual("A1", train.CurrentBlockId);
    }

    [TestMethod]
    public async Task EarlierArrival_RaisesSpeedWithinPermittedMaximum()
    {
        var (service, sender) = await DispatchTrainAsync();
        await service.UpdateDispatchedTrainPlanAsync("000", "A3", At(12, 15, 0));
        double slowerSpeed = Suggestions(sender)[^1].SuggestedSpeedMetersPerSecond;

        string? warning = await service.UpdateDispatchedTrainPlanAsync("000", "A3", At(12, 8, 0));

        double fasterSpeed = Suggestions(sender)[^1].SuggestedSpeedMetersPerSecond;
        Assert.IsNull(warning);
        Assert.AreEqual(100.0 / 480.0, fasterSpeed, 1e-9);
        Assert.IsGreaterThan(slowerSpeed, fasterSpeed);
        Assert.IsLessThanOrEqualTo(MaxBlueSpeed, fasterSpeed);
        Assert.HasCount(1, Requests(sender));
    }

    [TestMethod]
    public async Task UpdatedPlan_UsesCurrentSystemTime()
    {
        // At 12:05:00, 100 m to A3 by 12:15:00 leaves 600 s.
        var (service, sender) = await DispatchTrainAsync();
        await service.SetSystemTimeAsync(At(12, 5, 0));

        await service.UpdateDispatchedTrainPlanAsync("000", "A3", At(12, 15, 0));

        Assert.AreEqual(100.0 / 600.0, Suggestions(sender)[^1].SuggestedSpeedMetersPerSecond, 1e-9);
    }

    // ---- Unachievable arrival times ----

    [TestMethod]
    public async Task ImpossibleArrival_CapsSpeedAtLegalMaximumAndWarns()
    {
        // 100 m in 5 s would need 72 km/h on 50 km/h track.
        var (service, sender) = await DispatchTrainAsync();
        var events = new List<CtcStateChangedEventArgs>();
        service.StateChanged += (_, e) => events.Add(e);

        string? warning = await service.UpdateDispatchedTrainPlanAsync("000", "A3", At(12, 0, 5));

        Assert.IsNotNull(warning);
        Assert.AreEqual(
            "For Train 000, the requested arrival at A3 (12:00:05) cannot be met. Required: 72.0 km/h. Maximum allowed: 50.0 km/h. Suggested the maximum permitted speed.",
            warning);
        Assert.AreEqual(MaxBlueSpeed, Suggestions(sender)[^1].SuggestedSpeedMetersPerSecond, 1e-9);
        Assert.IsTrue(Suggestions(sender).All(suggestion => suggestion.SuggestedSpeedMetersPerSecond <= MaxBlueSpeed + 1e-9));

        var updated = events.Single(e => e.Kind == CtcStateChangeKind.TrainMovementPlanUpdated);
        Assert.AreEqual("000", updated.TrainId);
        Assert.AreEqual(warning, updated.Message);

        // The dispatcher's requested time is still the active target.
        Assert.AreEqual(At(12, 0, 5), service.State.DispatchedTrains.Single().TargetArrivalTime);
    }

    [TestMethod]
    public async Task PassedArrival_SuggestsLegalMaximumAndWarns()
    {
        var (service, sender) = await DispatchTrainAsync();
        await service.SetSystemTimeAsync(At(12, 20, 0));

        string? warning = await service.UpdateDispatchedTrainPlanAsync("000", "A3", At(12, 15, 0));

        Assert.AreEqual(
            "For Train 000, the requested arrival at A3 (12:15:00) has already passed; suggested the maximum permitted speed (50.0 km/h).",
            warning);
        Assert.AreEqual(MaxBlueSpeed, Suggestions(sender)[^1].SuggestedSpeedMetersPerSecond, 1e-9);
    }

    [TestMethod]
    public async Task ImpossibleArrival_OnFastTrack_CapsAtVehicleMaximum()
    {
        var (service, sender) = await DispatchTrainAsync(BlueLine.CreateLayout(speedLimitKph: 100));

        await service.UpdateDispatchedTrainPlanAsync("000", "A3", At(12, 0, 1));

        Assert.AreEqual(TrainPerformance.MaxSpeedMetersPerSecond, Suggestions(sender)[^1].SuggestedSpeedMetersPerSecond, 1e-9);
    }

    // ---- Destination change ----

    [TestMethod]
    public async Task NewDestination_RoutesFromCurrentBlockAndRecalculates()
    {
        var (service, sender) = await DispatchTrainAsync();

        string? warning = await service.UpdateDispatchedTrainPlanAsync("000", "C13", At(12, 10, 0));

        // A1 A2 A3 A4 A5 C11 C12 C13: 350 m to the start of C13 in 600 s; authority is all 8 blocks.
        Assert.IsNull(warning);
        var suggestion = Suggestions(sender)[^1];
        Assert.AreEqual(350.0 / 600.0, suggestion.SuggestedSpeedMetersPerSecond, 1e-9);
        Assert.AreEqual(400.0, suggestion.SuggestedAuthorityMeters);
        Assert.HasCount(1, Requests(sender));

        var train = service.State.DispatchedTrains.Single();
        Assert.AreEqual("C13", train.TargetBlockId);
        Assert.AreEqual(At(12, 10, 0), train.TargetArrivalTime);
    }

    [TestMethod]
    public async Task NewDestinationAndArrival_BothApplied()
    {
        var (service, sender) = await DispatchTrainAsync();

        await service.UpdateDispatchedTrainPlanAsync("000", "B7", At(12, 20, 0));

        // A1..B7 is 7 blocks: 300 m to the start of B7 in 1200 s.
        var train = service.State.DispatchedTrains.Single();
        Assert.AreEqual("B7", train.TargetBlockId);
        Assert.AreEqual(At(12, 20, 0), train.TargetArrivalTime);
        Assert.AreEqual(300.0 / 1200.0, train.SuggestedSpeedMetersPerSecond, 1e-9);
        Assert.AreEqual(350.0, train.SuggestedAuthorityMeters);
        Assert.AreEqual(train.SuggestedSpeedMetersPerSecond, Suggestions(sender)[^1].SuggestedSpeedMetersPerSecond);
    }

    [TestMethod]
    public async Task UpdatedPlan_DoesNotChangeOriginalSchedule()
    {
        var (service, _) = await DispatchTrainAsync();

        await service.UpdateDispatchedTrainPlanAsync("000", "C13", At(12, 10, 0));

        var scheduled = service.State.ScheduledTrains.Single();
        CollectionAssert.AreEqual(BlueLine.BranchB, scheduled.Route.Select(block => block.BlockId).ToArray());
        Assert.AreEqual(At(12, 10, 0), scheduled.Route[2].ArrivalTime);
    }

    // ---- Authority along the new route ----

    [TestMethod]
    public async Task OccupiedBlockOnNewRoute_StopsAuthorityBeforeIt()
    {
        // Route A1 -> A5 with A3 occupied: authority covers A1 and A2 only.
        var (service, sender) = await DispatchTrainAsync();
        service.State.FindBlock("A3")!.Occupancy = OccupancyState.Occupied;

        await service.UpdateDispatchedTrainPlanAsync("000", "A5", At(12, 10, 0));

        Assert.AreEqual(100.0, Suggestions(sender)[^1].SuggestedAuthorityMeters);
    }

    [TestMethod]
    public async Task ClosedBlockOnNewRoute_StopsAuthorityBeforeIt()
    {
        var (service, sender) = await DispatchTrainAsync();
        service.State.FindBlock("C12")!.ConfirmedMaintenanceState = MaintenanceState.Closed;

        await service.UpdateDispatchedTrainPlanAsync("000", "C13", At(12, 10, 0));

        // A1..C11 = 6 blocks.
        Assert.AreEqual(300.0, Suggestions(sender)[^1].SuggestedAuthorityMeters);
    }

    [TestMethod]
    public async Task OwnBlockReportedOccupied_DoesNotZeroAuthority()
    {
        // A1 is occupied by the train itself.
        var (service, sender) = await DispatchTrainAsync();
        service.State.FindBlock("A1")!.Occupancy = OccupancyState.Occupied;

        string? warning = await service.UpdateDispatchedTrainPlanAsync("000", "A5", At(12, 10, 0));

        Assert.IsNull(warning);
        Assert.AreEqual(250.0, Suggestions(sender)[^1].SuggestedAuthorityMeters);
    }

    [TestMethod]
    public async Task OwnBlockClosedForMaintenance_GivesZeroAuthorityAndWarns()
    {
        var (service, sender) = await DispatchTrainAsync();
        service.State.FindBlock("A1")!.RequestedMaintenanceState = MaintenanceState.Closed;

        string? warning = await service.UpdateDispatchedTrainPlanAsync("000", "A5", At(12, 10, 0));

        Assert.AreEqual(0.0, Suggestions(sender)[^1].SuggestedAuthorityMeters);
        Assert.IsNotNull(warning);
        Assert.Contains("closed for maintenance", warning);
    }

    // ---- Failures leave the plan unchanged ----

    private static void AssertPlanUnchanged(DispatchedTrainState train)
    {
        Assert.AreEqual("A3", train.TargetBlockId);
        Assert.AreEqual(At(12, 10, 0), train.TargetArrivalTime);
        Assert.AreEqual(100.0 / 600.0, train.SuggestedSpeedMetersPerSecond, 1e-9);
        Assert.AreEqual(500.0, train.SuggestedAuthorityMeters);
        Assert.AreEqual("A1", train.CurrentBlockId);
    }

    [TestMethod]
    public async Task UnreachableDestination_SendsNothingAndKeepsPlan()
    {
        // D16 is on the line but connected to nothing.
        var layout = BlueLine.CreateLayout();
        layout.Lines[0].Blocks.Add(new TrackBlockDefinition { BlockId = "D16", BlockNumber = 16, Section = "D", LengthMeters = 50, SpeedLimitKilometersPerHour = 50 });
        var (service, sender) = await DispatchTrainAsync(layout);
        var events = new List<CtcStateChangeKind>();
        service.StateChanged += (_, e) => events.Add(e.Kind);

        var ex = await Assert.ThrowsExactlyAsync<DispatchException>(
            () => service.UpdateDispatchedTrainPlanAsync("000", "D16", At(12, 10, 0)));

        Assert.AreEqual("No route on Blue Line connects Train 000's current block A1 to D16.", ex.Message);
        Assert.HasCount(2, sender.SentMessages);
        Assert.IsEmpty(events);
        AssertPlanUnchanged(service.State.DispatchedTrains.Single());
    }

    [TestMethod]
    public async Task SendFailure_KeepsOldPlan()
    {
        var (service, sender) = await DispatchTrainAsync();
        sender.ExceptionToThrow = new MessageSendException("Track Controller is not connected.");
        var events = new List<CtcStateChangeKind>();
        service.StateChanged += (_, e) => events.Add(e.Kind);

        await Assert.ThrowsExactlyAsync<MessageSendException>(
            () => service.UpdateDispatchedTrainPlanAsync("000", "C13", At(12, 20, 0)));

        Assert.IsEmpty(events);
        AssertPlanUnchanged(service.State.DispatchedTrains.Single());
    }

    [TestMethod]
    public async Task UnknownTrain_FailsCleanly()
    {
        var (service, sender) = await DispatchTrainAsync();

        var ex = await Assert.ThrowsExactlyAsync<DispatchException>(
            () => service.UpdateDispatchedTrainPlanAsync("007", "A3", At(12, 10, 0)));

        Assert.AreEqual("Train 007 has not been dispatched.", ex.Message);
        Assert.HasCount(2, sender.SentMessages);
    }

    [TestMethod]
    public async Task QueuedButNotDispatchedTrain_FailsCleanly()
    {
        var sender = new FakeMessageSender();
        var service = BlueLine.CreateService(sender);
        service.QueueSchedule([BlueLine.Train("000", At(12, 0, 0))]);

        await Assert.ThrowsExactlyAsync<DispatchException>(
            () => service.UpdateDispatchedTrainPlanAsync("000", "A3", At(12, 10, 0)));

        Assert.IsEmpty(sender.SentMessages);
        Assert.HasCount(1, service.State.DispatchQueue);
    }

    [TestMethod]
    public async Task DestinationNotOnLine_FailsCleanly()
    {
        var (service, sender) = await DispatchTrainAsync();

        var ex = await Assert.ThrowsExactlyAsync<DispatchException>(
            () => service.UpdateDispatchedTrainPlanAsync("000", "Z99", At(12, 10, 0)));

        Assert.AreEqual("Block Z99 is not on Blue Line.", ex.Message);
        Assert.HasCount(2, sender.SentMessages);
        AssertPlanUnchanged(service.State.DispatchedTrains.Single());
    }

    [TestMethod]
    public async Task DestinationIsCurrentBlock_FailsCleanly()
    {
        var (service, sender) = await DispatchTrainAsync();

        await Assert.ThrowsExactlyAsync<DispatchException>(
            () => service.UpdateDispatchedTrainPlanAsync("000", "A1", At(12, 10, 0)));

        Assert.HasCount(2, sender.SentMessages);
        AssertPlanUnchanged(service.State.DispatchedTrains.Single());
    }

    [TestMethod]
    public async Task RouteWithoutSpeedLimit_FailsCleanly()
    {
        var (service, sender) = await DispatchTrainAsync();
        service.State.FindBlock("A4")!.SpeedLimitKilometersPerHour = 0;

        var ex = await Assert.ThrowsExactlyAsync<DispatchException>(
            () => service.UpdateDispatchedTrainPlanAsync("000", "A5", At(12, 10, 0)));

        Assert.Contains("A4", ex.Message);
        Assert.HasCount(2, sender.SentMessages);
        AssertPlanUnchanged(service.State.DispatchedTrains.Single());
    }

    // ---- RouteManager ----

    private static CtcLineState Line(string lineId, params (string BlockId, string[] Connected)[] blocks)
    {
        var line = new CtcLineState { LineId = lineId, Name = lineId };
        foreach (var (blockId, connected) in blocks)
        {
            var block = new CtcBlockState { BlockId = blockId, LengthMeters = 50, SpeedLimitKilometersPerHour = 50 };
            foreach (var id in connected)
            {
                block.ConnectedBlockIds.Add(id);
            }

            line.Blocks.Add(block);
        }

        return line;
    }

    private static string[]? RouteIds(CtcLineState line, string from, string to) =>
        RouteManager.FindRoute(line, from, to)?.Select(block => block.BlockId).ToArray();

    [TestMethod]
    public void FindRoute_BlueLine_FollowsBranch()
    {
        var service = BlueLine.CreateService();
        var line = service.State.FindLine(BlueLine.LineId)!;

        CollectionAssert.AreEqual(new[] { "A1", "A2", "A3", "A4", "A5", "C11", "C12" }, RouteIds(line, "A1", "C12"));
        CollectionAssert.AreEqual(new[] { "B7", "B6", "A5", "C11" }, RouteIds(line, "B7", "C11"));
    }

    [TestMethod]
    public void FindRoute_Loop_TakesShortestWayAndTerminates()
    {
        // L1-L2-L3-L4-L5-L6-L1 loop, plus an island block.
        var line = Line(
            "LOOP",
            ("L1", ["L2", "L6"]),
            ("L2", ["L1", "L3"]),
            ("L3", ["L2", "L4"]),
            ("L4", ["L3", "L5"]),
            ("L5", ["L4", "L6"]),
            ("L6", ["L5", "L1"]),
            ("ISLAND", []));

        CollectionAssert.AreEqual(new[] { "L1", "L6", "L5" }, RouteIds(line, "L1", "L5"));
        Assert.IsNull(RouteManager.FindRoute(line, "L1", "ISLAND"));
    }

    [TestMethod]
    public void FindRoute_DoesNotCrossIntoAnotherLine()
    {
        // X1 connects to G1 on another line; G1 would be the only bridge to X3.
        var line = Line("X", ("X1", ["X2", "G1"]), ("X2", ["X1"]), ("X3", ["G1"]));

        Assert.IsNull(RouteManager.FindRoute(line, "X1", "X3"));
    }

    [TestMethod]
    public void FindRoute_BlockNotOnLine_Throws()
    {
        var line = Line("X", ("X1", ["X2"]), ("X2", ["X1"]));

        Assert.ThrowsExactly<ArgumentException>(() => RouteManager.FindRoute(line, "X1", "G1"));
        Assert.ThrowsExactly<ArgumentException>(() => RouteManager.FindRoute(line, "G1", "X1"));
    }

    [TestMethod]
    public void FindRoute_StartIsDestination_IsJustThatBlock()
    {
        var line = Line("X", ("X1", ["X2"]), ("X2", ["X1"]));

        CollectionAssert.AreEqual(new[] { "X1" }, RouteIds(line, "X1", "X1"));
    }

    // ---- AuthorityManager / SpeedPlanner building blocks ----

    private static List<CtcBlockState> Blocks(int count) =>
        Enumerable.Range(1, count).Select(n => new CtcBlockState { BlockId = $"B{n}", LengthMeters = 50, SpeedLimitKilometersPerHour = 50 }).ToList();

    [TestMethod]
    public void UpdatedAuthority_OwnBlockOccupied_IgnoresOnlyThatBlock()
    {
        var route = Blocks(5);
        route[0].Occupancy = OccupancyState.Occupied;

        Assert.AreEqual(250.0, AuthorityManager.CalculateUpdatedAuthorityMeters(route));
        Assert.AreEqual(0.0, AuthorityManager.CalculateInitialAuthorityMeters(route));

        route[3].Occupancy = OccupancyState.Occupied;

        Assert.AreEqual(150.0, AuthorityManager.CalculateUpdatedAuthorityMeters(route));
    }

    [TestMethod]
    public void SpeedToTarget_Achievable_IsDistanceOverRemainingTime()
    {
        // 200 m to the start of B5 in 40 s.
        var speed = SpeedPlanner.CalculateSpeedToTarget(Blocks(5), At(12, 0, 40), At(12, 0, 0));

        Assert.AreEqual(5.0, speed.SpeedMetersPerSecond, 1e-9);
        Assert.AreEqual("B5", speed.TargetBlockId);
        Assert.IsFalse(speed.IsLate);
        Assert.IsFalse(speed.IsCapped);
        Assert.IsNull(speed.ArrivalWarning);
    }

    [TestMethod]
    public void SpeedToTarget_ExactlyAtLimit_IsNotCapped()
    {
        // 200 m at 50 km/h takes 14.4 s.
        var speed = SpeedPlanner.CalculateSpeedToTarget(Blocks(5), At(12, 0, 0) + TimeSpan.FromSeconds(14.4), At(12, 0, 0));

        Assert.AreEqual(MaxBlueSpeed, speed.SpeedMetersPerSecond, 1e-9);
        Assert.IsFalse(speed.IsCapped);
    }

    [TestMethod]
    public void SpeedToTarget_SingleBlockRoute_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => SpeedPlanner.CalculateSpeedToTarget(Blocks(1), At(12, 1, 0), At(12, 0, 0)));
    }
}
