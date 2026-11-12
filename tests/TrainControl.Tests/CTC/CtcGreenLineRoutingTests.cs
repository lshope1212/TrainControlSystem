using CTC.Core.Dispatching;
using CTC.Core.Models;
using CTC.Core.Routing;
using CTC.Core.Scheduling;
using CTC.Core.Services;
using TrackModel.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.CTC;

/// <summary>
/// Topology-aware routing on the production course layout: the Green Line yard (GREEN-63) to Edgebrook
/// (GREEN-9) route, which revisits N (GREEN-77..85) and sets switches 77 and 85 both ways, its tracking by
/// route occurrence, and its movement-authority segments. Simpler Blue and Red routes keep working unchanged.
/// </summary>
[TestClass]
public sealed class CtcGreenLineRoutingTests
{
    private static readonly TimeSpan Departure = new(14, 0, 0);
    private static readonly TimeSpan Arrival = new(15, 0, 0);

    /// <summary>The expected yard-to-Edgebrook route on the current course layout (117 block visits).</summary>
    internal static readonly string[] YardToEdgebrook =
    [
        .. Green(63, 77),   // K-M, one way toward N
        .. Green(78, 85),   // N, first time (increasing)
        .. Green(86, 100),  // O-Q, one way round the loop
        .. Green(85, 77),   // N again, decreasing
        .. Green(101, 150), // R-Z, one way
        .. Green(28, 13),   // F-D, decreasing
        .. Green(12, 9),    // C to Edgebrook
    ];

    private static IEnumerable<string> Green(int from, int to) =>
        (from <= to ? Enumerable.Range(from, to - from + 1) : Enumerable.Range(to, from - to + 1).Reverse()).Select(n => $"GREEN-{n}");

    private static CTCService CourseService(FakeMessageSender? sender = null)
    {
        var service = new CTCService(sender ?? new FakeMessageSender());
        var model = new TrackService();
        CourseTrackLayout.Load(model);
        service.ApplyTrackLayout(model.CreateLayoutMessage());
        return service;
    }

    private static CtcLineState GreenLine(CTCService service) => service.State.FindLine("Green")!;

    /// <summary>What the dispatcher does in the Schedule Builder: Green, Train 001, start GREEN-63, arrival at Edgebrook only.</summary>
    private static ScheduleConversionResult ScheduleToEdgebrook(CTCService service, params (string BlockId, string Time)[] extraStops)
    {
        var template = ScheduleTemplateFactory.Create(GreenLine(service), TrainIds.Allocate(service.State, 1));
        Assert.AreEqual("GREEN-63", template.GetStartBlockId(0), "The Green Line's only yard entry is preselected.");
        template.Rows.Single(row => row.BlockId == "GREEN-63").SetTrainTime(0, "14:00:00");
        foreach (var (blockId, time) in extraStops)
        {
            template.Rows.Single(row => row.BlockId == blockId).SetTrainTime(0, time);
        }

        template.Rows.Single(row => row.BlockId == "GREEN-9").SetTrainTime(0, "15:00:00");
        return ScheduleTemplateConverter.Convert(template);
    }

    // ---- Test A: route generation ----

    [TestMethod]
    public void ScheduleBuilder_YardToEdgebrook_BuildsTheDirectedLoopRoute()
    {
        var service = CourseService();
        var result = ScheduleToEdgebrook(service);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        var train = result.Trains.Single();
        var route = train.Route.Select(block => block.BlockId).ToArray();
        Console.WriteLine(string.Join(" ", route));

        Assert.AreEqual("001", train.TrainId);
        Assert.AreEqual("GREEN-63", route[0]);
        Assert.AreEqual("GREEN-9", route[^1]);
        Assert.HasCount(117, route);
        CollectionAssert.AreEqual(YardToEdgebrook, route);

        // Only the dispatcher's two cells are timed; every other block was filled in by CTC.
        Assert.AreEqual(Departure, train.Route[0].ArrivalTime);
        Assert.AreEqual(Arrival, train.Route[^1].ArrivalTime);
        Assert.IsTrue(train.Route.Skip(1).SkipLast(1).All(block => !block.IsTimed));

        // The schedule is accepted for dispatch as it is.
        BlueLine.ReportAllClear(service);
        service.QueueSchedule(result.Trains);
        Assert.HasCount(1, service.State.DispatchQueue);
    }

    [TestMethod]
    public void YardToEdgebrookRoute_EveryStepIsConnectedOneWayCorrectAndPassesSwitchesStemToLeg()
    {
        var service = CourseService();
        var green = GreenLine(service);
        var topology = LineTopology.From(green);
        var route = ScheduleToEdgebrook(service).Trains.Single().Route.Select(block => block.BlockId).ToList();
        var blocks = green.Blocks.ToDictionary(block => block.BlockId);

        for (int i = 0; i + 1 < route.Count; i++)
        {
            Assert.Contains(route[i + 1], blocks[route[i]].ConnectedBlockIds.ToList(), $"{route[i]} -> {route[i + 1]} is connected.");
            Assert.IsFalse(topology.IsAgainstDirection(route[i], route[i + 1]), $"{route[i]} -> {route[i + 1]} follows the direction of travel.");
            if (i > 0)
            {
                Assert.AreNotEqual(route[i - 1], route[i + 1], $"No reversal at {route[i]}.");
                Assert.AreEqual(RouteStepProblem.None, topology.StepProblem(route[i - 1], route[i], route[i + 1]), $"{route[i - 1]} -> {route[i]} -> {route[i + 1]}");
            }
        }

        Assert.IsNull(topology.FindIllegalStep(route));
        Assert.IsEmpty(RouteGeometry.FindProblems(green, ScheduleToEdgebrook(service).Trains.Single()), "Nothing for the dispatcher to be warned about.");

        // Bounded: no routing state (block, approach) is ever repeated, so the route cannot loop endlessly.
        var states = Enumerable.Range(1, route.Count - 1).Select(i => (route[i - 1], route[i])).ToList();
        Assert.HasCount(states.Count, states.Distinct().ToList());

        // Switches: stem to leg, with both passages of 77 and 85.
        var passages = MovementSegmentation.SwitchPassages(topology, route, 0, route.Count - 1);
        string Describe(SwitchPassage passage) => $"{passage.SwitchBlockId}@{passage.RouteIndex}:{passage.RequiredPosition}";
        CollectionAssert.AreEqual(
            new[] { "GREEN-77@14:Normal", "GREEN-85@22:Normal", "GREEN-85@38:Reverse", "GREEN-77@46:Reverse", "GREEN-28@97:Reverse", "GREEN-13@112:Normal" },
            passages.Select(Describe).ToArray());
        foreach (var passage in passages)
        {
            string stem = topology.Stem(passage.SwitchBlockId)!;
            Assert.IsTrue(route[passage.RouteIndex - 1] == stem || route[passage.RouteIndex + 1] == stem, $"{Describe(passage)} passes its stem {stem}.");
        }

        // Repeated blocks are exactly N (77..85), each visited twice.
        var repeated = route.GroupBy(id => id).Where(group => group.Count() > 1).ToDictionary(group => group.Key, group => group.Count());
        CollectionAssert.AreEquivalent(Green(77, 85).ToArray(), repeated.Keys.ToArray());
        Assert.IsTrue(repeated.Values.All(count => count == 2));
    }

    [TestMethod]
    public void TimedIntermediateWaypoints_StillSelectTheRoute_AndOutOfOrderTimesAreRefused()
    {
        var service = CourseService();

        // A waypoint on the loop and one after N: the same legal route, now with three timed stops.
        var withStops = ScheduleToEdgebrook(service, ("GREEN-96", "14:20:00"), ("GREEN-123", "14:40:00"));
        Assert.IsTrue(withStops.IsSuccess, withStops.ErrorMessage);
        var route = withStops.Trains.Single().Route;
        CollectionAssert.AreEqual(YardToEdgebrook, route.Select(block => block.BlockId).ToArray());
        CollectionAssert.AreEqual(new[] { "GREEN-63", "GREEN-96", "GREEN-123", "GREEN-9" }, route.Where(block => block.IsTimed).Select(block => block.BlockId).ToArray());

        // A waypoint on N timed after one in R: the train cannot pass R and then come back to N.
        var outOfOrder = ScheduleToEdgebrook(service, ("GREEN-110", "14:20:00"), ("GREEN-80", "14:40:00"));
        Assert.IsFalse(outOfOrder.IsSuccess);
        StringAssert.Contains(outOfOrder.ErrorMessage, "GREEN-80");
    }

    [TestMethod]
    public void BlueAndRedRoutes_AreUnchanged()
    {
        var service = CourseService();
        IReadOnlyList<string>? Route(string line, string from, string to) =>
            RoutePlanner.FindRoute(LineTopology.From(service.State.FindLine(line)!), from, to);

        CollectionAssert.AreEqual(new[] { "1", "2", "3", "4", "5", "11", "12", "13", "14", "15" }, Route("Blue", "1", "15")!.ToArray(), "Blue 1 to Station C through switch 5 Reverse.");
        CollectionAssert.AreEqual(new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "10" }, Route("Blue", "1", "10")!.ToArray());
        // Red 9 to 28: the shorter of its two legal ways into RED-16 (its Normal leg RED-15), then the stem RED-17.
        CollectionAssert.AreEqual(
            new[] { "RED-9", "RED-10", "RED-11", "RED-12", "RED-13", "RED-14", "RED-15", "RED-16", "RED-17", "RED-18", "RED-19", "RED-20", "RED-21", "RED-22", "RED-23", "RED-24", "RED-25", "RED-26", "RED-27", "RED-28" },
            Route("Red", "RED-9", "RED-28")!.ToArray());

        // Entered from RED-1 (the Reverse leg) a train in RED-16 can only continue to the stem.
        CollectionAssert.AreEqual(new[] { "RED-16", "RED-17" }, RoutePlanner.FindRoute(LineTopology.From(service.State.FindLine("Red")!), "RED-16", "RED-17", "RED-1")!.ToArray());
        var toOtherLeg = RoutePlanner.FindRoute(LineTopology.From(service.State.FindLine("Red")!), "RED-16", "RED-15", "RED-1");
        Assert.AreNotEqual("RED-15", toOtherLeg?.ElementAtOrDefault(1), "Never leg to leg through RED-16.");

        // A Blue route is one segment, exactly as before: nothing to split.
        var blue = LineTopology.From(service.State.FindLine("Blue")!);
        var toC = Route("Blue", "1", "15")!;
        Assert.AreEqual(toC.Count - 1, MovementSegmentation.SegmentEnd(blue, toC, 0, toC.Count - 1));
    }

    [TestMethod]
    public void DispatcherReplan_ContinuesInTheTrainsDirectionOfTravel()
    {
        var service = CourseService();
        var green = GreenLine(service);

        // A train in GREEN-80 that came from GREEN-79 (first time along N) re-planned to GREEN-78: it cannot turn
        // back, so it goes on round the loop and comes back along N.
        var forward = RouteManager.FindRoute(green, "GREEN-80", "GREEN-78", "GREEN-79")!.Select(block => block.BlockId).ToArray();
        string[] expected = ["GREEN-80", .. Green(81, 85), .. Green(86, 100), .. Green(85, 78)];
        CollectionAssert.AreEqual(expected, forward);

        // The same block entered from the other side (coming back along N) is two blocks away.
        var back = RouteManager.FindRoute(green, "GREEN-80", "GREEN-78", "GREEN-81")!.Select(block => block.BlockId).ToArray();
        CollectionAssert.AreEqual(new[] { "GREEN-80", "GREEN-79", "GREEN-78" }, back);
    }

    // ---- Movement segmentation ----

    [TestMethod]
    public void YardToEdgebrook_IsCoveredByTwoSegments_SplitWhereSwitch85MustChange()
    {
        var topology = LineTopology.From(GreenLine(CourseService()));
        var route = YardToEdgebrook;

        // From the yard: through 77 and 85 Normal and round the loop, ending at GREEN-99, just before GREEN-100
        // re-enters switch 85's protection zone (84, 85, 86, 100), where 85 must be Reverse.
        int first = MovementSegmentation.SegmentEnd(topology, route, 0, route.Length - 1);
        Assert.AreEqual(36, first);
        Assert.AreEqual("GREEN-99", route[first]);

        // Still inside 85's zone (GREEN-86): the next segment cannot start yet.
        Assert.AreEqual(36, MovementSegmentation.SegmentEnd(topology, route, 23, route.Length - 1));

        // Out of it (GREEN-87): the rest of the journey is one segment, 85 and 77 Reverse, 28 Reverse, 13 Normal.
        Assert.AreEqual(route.Length - 1, MovementSegmentation.SegmentEnd(topology, route, 24, route.Length - 1));
        for (int start = 0; start < route.Length; start++)
        {
            int end = MovementSegmentation.SegmentEnd(topology, route, start, route.Length - 1);
            var segment = route.Skip(start).Take(end - start + 1).ToList();
            Assert.HasCount(segment.Count, segment.Distinct().ToList(), $"Segment from {start} never revisits a block.");
        }
    }

    // ---- Test B: repeated-block tracking ----

    [TestMethod]
    public async Task Tracking_FollowsRouteOccurrences_NotBlockIds_AroundTheRepeatedN()
    {
        var sender = new FakeMessageSender();
        var service = CourseService(sender);
        BlueLine.ReportAllClear(service);
        service.QueueSchedule(ScheduleToEdgebrook(service).Trains);
        await service.SetSystemTimeAsync(Departure);
        await BlueLine.PlaceTrainAsync(service, "GREEN-63");
        var train = service.State.FindDispatchedTrain("001")!;
        Assert.AreEqual(TrainReleaseStatus.DetectedOnTrack, train.ReleaseStatus);

        var route = YardToEdgebrook;
        for (int i = 1; i < route.Length; i++)
        {
            BlueLine.Report(service, route[i], OccupancyState.Occupied);
            BlueLine.Report(service, route[i - 1], OccupancyState.Clear);
            await service.SynchronizeMovementRequestsAsync();
            Assert.AreEqual(i, train.ActiveRouteIndex, $"Tracked into route block {i} ({route[i]}).");
            Assert.AreEqual(route[i], train.CurrentBlockId);
            Assert.AreEqual(route[i - 1], train.PreviousBlockId);

            switch (i)
            {
                case 14: Assert.AreEqual("GREEN-76", train.PreviousBlockId, "First GREEN-77: from the yard side."); break;
                case 22: Assert.AreEqual("GREEN-84", train.PreviousBlockId, "First GREEN-85: from N."); break;
                case 38: Assert.AreEqual("GREEN-100", train.PreviousBlockId, "Second GREEN-85: from the loop."); break;
                case 46: Assert.AreEqual("GREEN-78", train.PreviousBlockId, "Second GREEN-77: back along N."); break;
            }

            if (i == 30)
            {
                // GREEN-85 is on the route again later, but the train in GREEN-93 continues to GREEN-94: a GREEN-85
                // occupancy now is something else and moves nothing.
                BlueLine.Report(service, "GREEN-85", OccupancyState.Occupied);
                await service.SynchronizeMovementRequestsAsync();
                Assert.AreEqual(30, train.ActiveRouteIndex);
                BlueLine.Report(service, "GREEN-85", OccupancyState.Clear);
            }

            if (i == 38)
            {
                // In the second GREEN-85 the next block is GREEN-84, not GREEN-86 as the first time.
                BlueLine.Report(service, "GREEN-86", OccupancyState.Occupied);
                await service.SynchronizeMovementRequestsAsync();
                Assert.AreEqual(38, train.ActiveRouteIndex);
                BlueLine.Report(service, "GREEN-86", OccupancyState.Clear);
            }

            if (i < route.Length - 1)
            {
                Assert.AreEqual(TrainMovementPhase.EnRoute, train.Phase, $"Not arrived at {route[i]} (route block {i}).");
            }
        }

        Assert.AreEqual(116, train.ActiveRouteIndex);
        Assert.AreNotEqual(TrainMovementPhase.EnRoute, train.Phase, "Arrived at Edgebrook only at the end of the route.");
    }

    // ---- Test C (CTC side): bounded requests and the explicit next segment ----

    [TestMethod]
    public async Task Requests_AreBoundedSegments_AndTheNextSegmentIsRequestedOnceTheTrainLeavesSwitch85sZone()
    {
        var sender = new FakeMessageSender();
        var service = CourseService(sender);
        BlueLine.ReportAllClear(service);
        service.QueueSchedule(ScheduleToEdgebrook(service).Trains);
        await service.SetSystemTimeAsync(Departure);
        await BlueLine.PlaceTrainAsync(service, "GREEN-63");
        var train = service.State.FindDispatchedTrain("001")!;
        var route = YardToEdgebrook;

        Assert.AreEqual(36, train.AuthorityLimitRouteIndex, "The first segment ends at GREEN-99.");
        Assert.AreEqual(116, train.MovementEndRouteIndex, "The movement itself runs to Edgebrook.");
        var first = sender.SentMessages.OfType<MovementSuggestionMessage>().Single(message => message.IsActive);
        Assert.AreEqual("GREEN-63", first.TargetBlockId);
        CollectionAssert.AreEqual(route.Skip(1).Take(36).ToArray(), first.PathBlockIds.ToArray(), "GREEN-64 .. GREEN-99.");

        // Up to GREEN-87, with the rear still in GREEN-86 (switch 85's zone): requests stay within the first segment.
        for (int i = 1; i <= 24; i++)
        {
            BlueLine.Report(service, route[i], OccupancyState.Occupied);
            if (i < 24)
            {
                BlueLine.Report(service, route[i - 1], OccupancyState.Clear);
            }

            await service.SynchronizeMovementRequestsAsync();
        }

        Assert.AreEqual(24, train.ActiveRouteIndex);
        Assert.AreEqual(36, train.AuthorityLimitRouteIndex, "Not extended while GREEN-86 is occupied.");
        StringAssert.Contains(train.NextSegmentWaitReason, "switch GREEN-85 must be set Reverse");
        StringAssert.Contains(train.NextSegmentWaitReason, "GREEN-86");
        int before = sender.SentMessages.Count;

        // The rear clears GREEN-86: the next segment is one explicit new request, after cancelling the old one.
        BlueLine.Report(service, "GREEN-86", OccupancyState.Clear);
        await service.SynchronizeMovementRequestsAsync();

        Assert.AreEqual(116, train.AuthorityLimitRouteIndex);
        Assert.AreEqual(string.Empty, train.NextSegmentWaitReason);
        var sent = sender.SentMessages.Skip(before).OfType<MovementSuggestionMessage>().ToList();
        Assert.HasCount(2, sent);
        Assert.IsFalse(sent[0].IsActive, "The first segment's request is cancelled first.");
        var next = sent[1];
        Assert.IsTrue(next.IsActive);
        Assert.IsNull(next.PredecessorRequestId, "An ordinary request, fully evaluated: not a successor of the old one.");
        Assert.AreNotEqual(sent[0].RequestId, next.RequestId);
        Assert.AreEqual("GREEN-87", next.TargetBlockId);
        Assert.AreEqual("GREEN-86", next.EntryBlockId);
        CollectionAssert.AreEqual(route.Skip(25).ToArray(), next.PathBlockIds.ToArray(), "GREEN-88 through the second N, R-Z and F-D to Edgebrook.");

        // Every request CTC ever sent: never a repeated block.
        foreach (var message in sender.SentMessages.OfType<MovementSuggestionMessage>().Where(message => message.IsActive))
        {
            var path = new[] { message.TargetBlockId }.Concat(message.PathBlockIds).ToList();
            Assert.HasCount(path.Count, path.Distinct().ToList(), $"Request {message.RequestId} revisits no block.");
        }
    }

    [TestMethod]
    public async Task NextSegment_WaitsWhileSwitch85sZoneIsBlocked_AndContinuesWhenItClears()
    {
        var sender = new FakeMessageSender();
        var service = CourseService(sender);
        BlueLine.ReportAllClear(service);
        service.QueueSchedule(ScheduleToEdgebrook(service).Trains);
        await service.SetSystemTimeAsync(Departure);
        await BlueLine.PlaceTrainAsync(service, "GREEN-63");
        var train = service.State.FindDispatchedTrain("001")!;
        var route = YardToEdgebrook;
        for (int i = 1; i <= 23; i++)
        {
            BlueLine.Report(service, route[i], OccupancyState.Occupied);
            BlueLine.Report(service, route[i - 1], OccupancyState.Clear);
            await service.SynchronizeMovementRequestsAsync();
        }

        // Something else occupies GREEN-84 (in 85's zone, behind the train) while the train runs round the loop.
        BlueLine.Report(service, "GREEN-84", OccupancyState.Occupied);
        for (int i = 24; i <= 36; i++)
        {
            BlueLine.Report(service, route[i], OccupancyState.Occupied);
            BlueLine.Report(service, route[i - 1], OccupancyState.Clear);
            await service.SynchronizeMovementRequestsAsync();
            Assert.AreEqual(36, train.AuthorityLimitRouteIndex, $"Held in the first segment at {route[i]}.");
            StringAssert.Contains(train.NextSegmentWaitReason, "GREEN-84");
            var request = train.MovementRequest!.Plan;
            Assert.IsFalse(request.PathBlockIds.Contains("GREEN-100"), "Never asks to enter switch 85's zone with the switch still set for the first passage.");
        }

        Assert.AreEqual("GREEN-99", train.CurrentBlockId);
        BlueLine.Report(service, "GREEN-84", OccupancyState.Clear);
        await service.SynchronizeMovementRequestsAsync();
        Assert.AreEqual(116, train.AuthorityLimitRouteIndex);
        Assert.AreEqual("GREEN-100", train.MovementRequest!.Plan.PathBlockIds[0]);
    }

    [TestMethod]
    public void RouteGeometry_DescribesRoutesGivenAnotherWay()
    {
        var service = CourseService();
        var green = GreenLine(service);
        ScheduledTrain Train(params string[] blocks)
        {
            var train = new ScheduledTrain { TrainId = "001", LineId = "Green" };
            foreach (var id in blocks)
            {
                train.Route.Add(new ScheduledRouteBlock { BlockId = id });
            }

            return train;
        }

        StringAssert.StartsWith(RouteGeometry.FindProblems(green, Train("GREEN-63", "GREEN-62", "GREEN-61")).Single(), "Train 001's route moves GREEN-63 → GREEN-62 against Green Line's direction of travel (2 such steps)");
        StringAssert.StartsWith(RouteGeometry.FindProblems(green, Train("GREEN-76", "GREEN-77", "GREEN-101")).Single(), "Train 001's route passes switch GREEN-77 from GREEN-76 to GREEN-101, two legs of the switch");
        Assert.IsEmpty(RouteGeometry.FindProblems(green, Train(YardToEdgebrook)));
    }
}
