using TrackModel.Core.Models;
using TrackModel.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.TrackModel;

/// <summary>
/// The Track Model turns the Train Model's cumulative front position (and the train's length) into
/// block occupancy along the route registered at placement: the head block holds the front, trailing
/// blocks stay occupied until the rear has left them, and nothing invalid ever moves a train.
/// </summary>
[TestClass]
public class TrainPositionTrackingTests
{
    private const double Flexity = 32.2;

    /// <summary>
    /// Line T: A(100) B(75) C(120) D(10) E(10) F(10) G(200) H(100), in a chain.
    /// Line S: P(100) - switch S(50) with Normal leg N1(100)-N2(100) and Reverse leg R1(100)-R2(100).
    /// </summary>
    private static TrackService CreateService()
    {
        var blocks = new List<TrackBlock>();
        void Chain(string line, params (string Id, double Length)[] chain)
        {
            for (int i = 0; i < chain.Length; i++)
            {
                var connections = new List<string>();
                if (i > 0) connections.Add(chain[i - 1].Id);
                if (i + 1 < chain.Length) connections.Add(chain[i + 1].Id);
                blocks.Add(new TrackBlock { Id = chain[i].Id, LineId = line, Number = blocks.Count + 1, LengthMeters = chain[i].Length, ConnectedBlockIds = connections });
            }
        }

        Chain("T", ("A", 100), ("B", 75), ("C", 120), ("D", 10), ("E", 10), ("F", 10), ("G", 200), ("H", 100));
        Chain("S", ("P", 100), ("S", 50));
        Chain("S", ("N1", 100), ("N2", 100));
        Chain("S", ("R1", 100), ("R2", 100));
        var sw = blocks.Single(b => b.Id == "S");
        sw.HasSwitch = true;
        sw.NormalNextBlockId = "N1";
        sw.ReverseNextBlockId = "R1";
        sw.ConnectedBlockIds = ["P", "N1", "R1"];
        blocks.Single(b => b.Id == "N1").ConnectedBlockIds.Insert(0, "S");
        blocks.Single(b => b.Id == "R1").ConnectedBlockIds.Insert(0, "S");

        var service = new TrackService();
        service.LoadLayout(new TrackLayout { Name = "Position test", Blocks = blocks });
        return service;
    }

    private static TrainDispatchRequestMessage Dispatch(string trainId, string line, params string[] route) => new()
    {
        RequestId = "dispatch-" + trainId,
        TrainId = trainId,
        LineId = line,
        StartBlockId = route[0],
        RouteBlockIds = [.. route],
    };

    private static TrainStateMessage Report(string trainId, double position, long sequence, double length = Flexity, double speed = 10) => new()
    {
        TrainId = trainId,
        PositionMeters = position,
        SpeedMetersPerSecond = speed,
        TrainLengthMeters = length,
        Sequence = sequence,
        DispatchRequestId = "dispatch-" + trainId,
    };

    /// <summary>Dispatches and places <paramref name="trainId"/> with its initial report at 0 (sequence 1).</summary>
    private static TrackService Placed(string trainId = "001", string line = "T", params string[] route)
    {
        var service = CreateService();
        Place(service, trainId, line, route.Length > 0 ? route : ["A", "B", "C", "D", "E", "F", "G", "H"]);
        return service;
    }

    private static void Place(TrackService service, string trainId, string line, params string[] route)
    {
        Assert.AreEqual(TrainPlacementOutcome.Registered, service.RequestTrainPlacement(Dispatch(trainId, line, route)).Outcome);
        Assert.AreEqual(TrainPositionOutcome.Placed, service.ApplyTrainPosition(Report(trainId, 0, 1, speed: 0)).Outcome);
    }

    private static string[] Occupied(TrackService service) =>
        service.Layout.Blocks.Where(b => b.IsOccupied).Select(b => b.Id).ToArray();

    /// <summary>Advances the train in small steps (as the Train Model's 0.1 s ticks would).</summary>
    private static long Drive(TrackService service, string trainId, double from, double to, long sequence, double step = 1.5)
    {
        for (double position = from + step; position < to; position += step)
        {
            Assert.AreEqual(TrainPositionOutcome.Applied, service.ApplyTrainPosition(Report(trainId, position, ++sequence)).Outcome, $"at {position:0.0} m");
        }

        Assert.AreEqual(TrainPositionOutcome.Applied, service.ApplyTrainPosition(Report(trainId, to, ++sequence)).Outcome, $"at {to:0.0} m");
        return sequence;
    }

    [TestMethod]
    public void Dispatch_RegistersWithoutOccupyingAnything_UntilTheInitialReport()
    {
        var service = CreateService();

        var placement = service.RequestTrainPlacement(Dispatch("001", "T", "A", "B", "C"));

        Assert.AreEqual(TrainPlacementOutcome.Registered, placement.Outcome);
        Assert.IsEmpty(Occupied(service), "Sending a dispatch is not a physical placement.");
        Assert.AreEqual(TrainTrackingStatus.PendingPlacement, service.FindTrackedTrain("001")!.Status);
    }

    [TestMethod]
    public void PositionZero_OccupiesOnlyTheStartBlock_NeverABlockBehindIt()
    {
        var service = CreateService();
        service.RequestTrainPlacement(Dispatch("001", "T", "B", "C", "D"));

        var placed = service.ApplyTrainPosition(Report("001", 0, 1, speed: 0));

        Assert.AreEqual(TrainPositionOutcome.Placed, placed.Outcome);
        CollectionAssert.AreEqual(new[] { "B" }, Occupied(service), "The rear (-32.2 m) is in the yard: block A behind the start is not occupied.");
        Assert.IsTrue(service.FindBlock("B")!.IsTrainHead);
        Assert.AreEqual("001", service.FindBlock("B")!.TrainId);
        Assert.AreEqual(TrainTrackingStatus.Active, service.FindTrackedTrain("001")!.Status);
    }

    [TestMethod]
    public void MovementInsideOneBlock_ChangesNoOccupancy()
    {
        var service = Placed();
        var transitions = new List<TrackOccupancyTransitionEventArgs>();
        service.OccupancyTransition += (_, e) => transitions.Add(e);

        Drive(service, "001", 0, 99.9, 1);

        Assert.IsEmpty(transitions);
        CollectionAssert.AreEqual(new[] { "A" }, Occupied(service));
    }

    [TestMethod]
    public void TheWorkedExample_115m_OccupiesAAndBWithTheHeadInB_ThenOnlyBOnceTheRearLeavesA()
    {
        var service = Placed();

        long sequence = Drive(service, "001", 0, 115, 1);

        // Front 115 m: 15 m into B. Rear 115 - 32.2 = 82.8 m: still in A.
        CollectionAssert.AreEqual(new[] { "A", "B" }, Occupied(service));
        Assert.IsTrue(service.FindBlock("B")!.IsTrainHead);
        Assert.IsFalse(service.FindBlock("A")!.IsTrainHead);
        Assert.AreEqual("A", service.FindBlock("B")!.TrainEntryBlockId, "B was entered from A (continuation codes apply across that boundary).");
        var tracked = service.FindTrackedTrain("001")!;
        Assert.AreEqual("B", tracked.HeadBlockId);
        CollectionAssert.AreEqual(new[] { "A", "B" }, tracked.OccupiedBlockIds.ToArray());

        // The rear passes 100 m (front 132.2 m): A clears, and only A.
        Drive(service, "001", 115, 133, sequence);
        CollectionAssert.AreEqual(new[] { "B" }, Occupied(service));
    }

    [TestMethod]
    public void TrailingBlock_StaysOccupiedUntilTheRearFullyLeavesIt()
    {
        var service = Placed();
        long sequence = Drive(service, "001", 0, 132.1, 1);
        CollectionAssert.AreEqual(new[] { "A", "B" }, Occupied(service), "Rear at 99.9 m is still inside A.");

        service.ApplyTrainPosition(Report("001", 132.2, ++sequence));
        CollectionAssert.AreEqual(new[] { "B" }, Occupied(service), "Rear exactly at A's end: A is clear.");
    }

    [TestMethod]
    public void TrainLongerThanBlocks_OccupiesAllThreeShortBlocks()
    {
        var service = Placed();

        // D, E, F are 10 m each, starting at 295 m. Front 324 m (in F), rear 291.8 m (in C).
        Drive(service, "001", 0, 324, 1);

        CollectionAssert.AreEqual(new[] { "C", "D", "E", "F" }, Occupied(service));
        Assert.AreEqual("F", service.FindTrackedTrain("001")!.HeadBlockId);
        Assert.HasCount(1, service.Layout.Blocks.Where(b => b.IsTrainHead).ToList(), "One head per train.");
    }

    [TestMethod]
    public void CoalescedReport_ReconstructsEveryBoundaryInOrder()
    {
        var service = Placed();
        var steps = new List<string>();
        service.OccupancyTransition += (_, e) => steps.Add($"+{string.Join(",", e.Occupied)} -{string.Join(",", e.Cleared)} head {e.HeadBlockId}");

        // One report jumps from 0 to 320 m after 12 s of simulated time (the transport kept only the newest report).
        var result = service.ApplyTrainPosition(Report("001", 320, 1 + 120));

        Assert.AreEqual(TrainPositionOutcome.Applied, result.Outcome);
        CollectionAssert.AreEqual(new[]
        {
            "+B - head B",   // front enters B at 100 m
            "+ -A head B",   // rear leaves A at 132.2 m
            "+C - head C",   // front enters C at 175 m
            "+ -B head C",   // rear leaves B at 207.2 m
            "+D - head D",   // 295 m
            "+E - head E",   // 305 m
            "+F - head F",   // 315 m
        }, steps.ToArray());
        CollectionAssert.AreEqual(new[] { "C", "D", "E", "F" }, Occupied(service));
    }

    [TestMethod]
    public void PositionMessages_NeverCarryABlockId()
    {
        var names = typeof(TrainStateMessage).GetProperties().Select(p => p.Name).ToArray();
        Assert.IsFalse(names.Any(name => name.Contains("Block", StringComparison.OrdinalIgnoreCase)), string.Join(", ", names));
    }

    [TestMethod]
    public void WaysideCommands_NeverMoveATrain_OnlyTrainModelTelemetryDoes()
    {
        var service = Placed();
        service.ApplyCommandBatch(new TrackModelCommandBatchMessage
        {
            Sequence = 1,
            Commands =
            [
                new() { BlockId = "A", CommandedSpeedMetersPerSecond = 15, AuthorityMeters = 400, MovementRequestId = "P", Signal = SignalState.Green, Switch = SwitchPosition.Normal, Crossing = CrossingState.Open },
            ],
        }, DateTimeOffset.UtcNow);

        CollectionAssert.AreEqual(new[] { "A" }, Occupied(service));
        Assert.AreEqual(0, service.FindTrackedTrain("001")!.FrontPositionMeters, "Commanded speed and authority are never integrated into a position.");
        Assert.AreEqual(400, service.CaptureTrainCodes()["001"].Authority);
    }

    [TestMethod]
    public void MultiBlockTrain_HasExactlyOneEnvironmentAndOneCodeEntry_FromItsHead()
    {
        var service = Placed();
        Drive(service, "001", 0, 120, 1);
        service.ApplyCommandBatch(new TrackModelCommandBatchMessage
        {
            Sequence = 1,
            Commands =
            [
                new() { BlockId = "A", CommandedSpeedMetersPerSecond = 0, AuthorityMeters = 0, Signal = SignalState.Red, Switch = SwitchPosition.Normal, Crossing = CrossingState.Open },
                new() { BlockId = "B", CommandedSpeedMetersPerSecond = 12, AuthorityMeters = 250, MovementRequestId = "S", Signal = SignalState.Green, Switch = SwitchPosition.Normal, Crossing = CrossingState.Open },
            ],
        }, DateTimeOffset.UtcNow);

        var codes = service.CaptureTrainCodes(); // previously a duplicate-key exception for a train in two blocks
        Assert.HasCount(1, codes);
        Assert.AreEqual(("B", 12d, 250d, "S"), codes["001"]);

        var environments = service.Layout.Blocks.Select(b => service.CreateTrainEnvironment(b.Id)).Where(e => e.TrainId == "001").ToList();
        Assert.HasCount(1, environments, "A trailing block never names the train, so the Train Model gets one environment.");
        Assert.AreEqual("B", environments[0].BlockId);
        Assert.AreEqual(250d, environments[0].AuthorityMeters);
        Assert.AreEqual(string.Empty, service.CreateTrainEnvironment("A").TrainId);
    }

    [TestMethod]
    public void TwoTrains_DispatchIndependently_AndCannotShareABlock()
    {
        var service = CreateService();
        Place(service, "001", "T", "C", "D", "E", "F", "G");
        Assert.AreEqual(TrainPlacementOutcome.Rejected, service.RequestTrainPlacement(Dispatch("002", "T", "C", "D")).Outcome, "Its start block is occupied.");
        Assert.IsTrue(service.RequestTrainPlacement(Dispatch("002", "T", "C", "D")).IsRetryable);

        Place(service, "002", "T", "A", "B", "C", "D");
        CollectionAssert.AreEqual(new[] { "A", "C" }, Occupied(service));
        Assert.AreEqual("002", service.FindBlock("A")!.TrainId);
        Assert.AreEqual("001", service.FindBlock("C")!.TrainId);

        // 002 runs up behind 001: its front would enter C, still 001's block. Refused and faulted; nothing cleared.
        long sequence = Drive(service, "002", 0, 174, 1);
        var collision = service.ApplyTrainPosition(Report("002", 176, ++sequence));
        Assert.AreEqual(TrainPositionOutcome.Faulted, collision.Outcome, collision.Detail);
        Assert.AreEqual("001", service.FindBlock("C")!.TrainId, "002 never takes over 001's block.");
        CollectionAssert.AreEqual(new[] { "B", "C" }, Occupied(service));
        Assert.AreEqual("002", service.FindBlock("B")!.TrainId);
    }

    [TestMethod]
    public void RetiringATrain_ClearsExactlyItsFootprint()
    {
        var service = CreateService();
        Place(service, "001", "T", "C", "D", "E", "F", "G");
        Place(service, "002", "T", "A", "B", "C");
        Drive(service, "002", 0, 110, 1);
        CollectionAssert.AreEqual(new[] { "A", "B", "C" }, Occupied(service));

        Assert.IsTrue(service.RetireTrain("002", "test"));

        CollectionAssert.AreEqual(new[] { "C" }, Occupied(service), "Only 002's blocks cleared; 001 keeps C.");
        Assert.IsNull(service.FindTrackedTrain("002"));
        Assert.AreEqual(TrainPositionOutcome.Rejected, service.ApplyTrainPosition(Report("002", 111, 200)).Outcome, "A retired train's telemetry moves nothing.");
    }

    [TestMethod]
    public void InvalidTelemetry_NeverMovesATrainOrClearsABlock()
    {
        var service = Placed();
        long sequence = Drive(service, "001", 0, 110, 1);
        string[] before = Occupied(service);
        var front = service.FindTrackedTrain("001")!.FrontPositionMeters;

        var rejected = new (string Why, TrainStateMessage Report)[]
        {
            ("NaN position", Report("001", double.NaN, sequence + 1)),
            ("infinite position", Report("001", double.PositiveInfinity, sequence + 1)),
            ("negative position", Report("001", -3, sequence + 1)),
            ("zero length", Report("001", 111, sequence + 1, length: 0)),
            ("negative length", Report("001", 111, sequence + 1, length: -32.2)),
            ("NaN speed", Report("001", 111, sequence + 1, speed: double.NaN)),
            ("stale sequence", Report("001", 111, sequence - 1)),
            ("same sequence, different position", Report("001", 111, sequence)),
            ("unknown train", Report("009", 111, sequence + 1)),
            ("another dispatch's telemetry", new TrainStateMessage { TrainId = "001", PositionMeters = 111, TrainLengthMeters = Flexity, Sequence = sequence + 1, DispatchRequestId = "old" }),
        };
        foreach (var (why, report) in rejected)
        {
            Assert.AreEqual(TrainPositionOutcome.Rejected, service.ApplyTrainPosition(report).Outcome, why);
            CollectionAssert.AreEqual(before, Occupied(service), why);
            Assert.AreEqual(front, service.FindTrackedTrain("001")!.FrontPositionMeters, why);
        }

        Assert.AreEqual(TrainPositionOutcome.Duplicate, service.ApplyTrainPosition(Report("001", 110, sequence)).Outcome, "An exact repeat is harmless.");
        Assert.AreEqual(TrainTrackingStatus.Active, service.FindTrackedTrain("001")!.Status, "Rejected reports do not fault the train.");
        Assert.AreEqual(TrainPositionOutcome.Applied, service.ApplyTrainPosition(Report("001", 111, sequence + 1)).Outcome, "Valid telemetry still applies afterwards.");
    }

    [TestMethod]
    [DataRow("jump", 500.0, DisplayName = "Impossible forward jump")]
    [DataRow("reset", 0.0, DisplayName = "Reset to zero (impossible backward jump)")]
    [DataRow("length", 111.0, DisplayName = "Length discontinuity")]
    public void Discontinuities_FaultTheTrainAndHoldItsOccupancy(string kind, double position)
    {
        var service = Placed();
        long sequence = Drive(service, "001", 0, 110, 1);
        string[] before = Occupied(service);

        var result = service.ApplyTrainPosition(Report("001", position, sequence + 1, length: kind == "length" ? 40 : Flexity));

        Assert.AreEqual(TrainPositionOutcome.Faulted, result.Outcome, result.Detail);
        CollectionAssert.AreEqual(before, Occupied(service), "A faulted train's footprint is held, never cleared.");
        Assert.AreEqual(TrainTrackingStatus.Faulted, service.FindTrackedTrain("001")!.Status);
        Assert.AreEqual(TrainPositionOutcome.Rejected, service.ApplyTrainPosition(Report("001", 111, sequence + 2)).Outcome, "No further telemetry is applied.");
        CollectionAssert.AreEqual(before, Occupied(service));
    }

    [TestMethod]
    public void TelemetryBeforePlacement_IsRejected()
    {
        var service = CreateService();
        Assert.AreEqual(TrainPositionOutcome.Rejected, service.ApplyTrainPosition(Report("001", 0, 1)).Outcome);

        service.RequestTrainPlacement(Dispatch("001", "T", "A", "B"));
        Assert.AreEqual(TrainPositionOutcome.Rejected, service.ApplyTrainPosition(Report("001", 50, 1)).Outcome, "A newly activated train reports 0, not 50 m.");
        Assert.IsEmpty(Occupied(service));
    }

    [TestMethod]
    public void PositionBeyondTheRoute_IsNotInventedIntoTheNextBlock()
    {
        var service = Placed("001", "T", "A", "B");
        long sequence = Drive(service, "001", 0, 175.5, 1);
        CollectionAssert.AreEqual(new[] { "B" }, Occupied(service), "A small overshoot at the route's end stays in its last block.");

        var beyond = service.ApplyTrainPosition(Report("001", 178, sequence + 1));

        Assert.AreEqual(TrainPositionOutcome.Faulted, beyond.Outcome, beyond.Detail);
        CollectionAssert.AreEqual(new[] { "B" }, Occupied(service), "C is not occupied on the train's behalf.");
    }

    [TestMethod]
    public void RepeatedPlacement_IsIdempotent_AndAnOperatingTrainIsNeverReset()
    {
        var service = Placed();
        long sequence = Drive(service, "001", 0, 50, 1);

        var retry = service.RequestTrainPlacement(Dispatch("001", "T", "A", "B", "C", "D", "E", "F", "G", "H"));
        Assert.AreEqual(TrainPlacementOutcome.AlreadyPlaced, retry.Outcome);

        var redispatch = service.RequestTrainPlacement(new TrainDispatchRequestMessage { RequestId = "another", TrainId = "001", LineId = "T", StartBlockId = "A", RouteBlockIds = ["A", "B"] });
        Assert.AreEqual(TrainPlacementOutcome.Rejected, redispatch.Outcome, "A different dispatch of an operating train would reset it.");
        Assert.AreEqual(50, service.FindTrackedTrain("001")!.FrontPositionMeters);
        Assert.AreEqual(TrainPositionOutcome.Applied, service.ApplyTrainPosition(Report("001", 51, sequence + 1)).Outcome);
    }

    [TestMethod]
    public void InvalidRoutes_AreRejectedAtDispatch()
    {
        var service = CreateService();
        Assert.Contains("not connected", service.RequestTrainPlacement(Dispatch("001", "T", "A", "C")).Detail);
        Assert.Contains("reverses", service.RequestTrainPlacement(Dispatch("001", "T", "A", "B", "A")).Detail);
        Assert.Contains("not in the track layout", service.RequestTrainPlacement(Dispatch("001", "T", "A", "Z")).Detail);
        Assert.Contains("Line", service.RequestTrainPlacement(Dispatch("001", "T", "A", "P")).Detail);
        Assert.Contains("through switch S", service.RequestTrainPlacement(Dispatch("001", "S", "N1", "S", "R1")).Detail, "Leg to leg through a switch is not a path.");
        Assert.Contains("dispatchable", service.RequestTrainPlacement(Dispatch("011", "T", "A", "B")).Detail);
        Assert.AreEqual(TrainPlacementOutcome.Rejected, service.RequestTrainPlacement(new TrainDispatchRequestMessage { RequestId = "x", TrainId = "001", LineId = "T", StartBlockId = "B", RouteBlockIds = ["A", "B"] }).Outcome);
        Assert.IsEmpty(service.TrackedTrains);
    }

    [TestMethod]
    public void FacingSwitch_FollowsItsAlignment_AlongTheRoute()
    {
        var service = CreateService();
        service.ApplyCommand(new() { BlockId = "S", Switch = SwitchPosition.Reverse });
        Place(service, "001", "S", "P", "S", "R1", "R2");

        Drive(service, "001", 0, 160, 1);

        CollectionAssert.AreEqual(new[] { "S", "R1" }, Occupied(service));
        Assert.AreEqual("R1", service.FindTrackedTrain("001")!.HeadBlockId);
    }

    [TestMethod]
    public void FacingSwitch_SetAgainstTheRoute_PutsTheTrainWhereTheTrackGoes_AndFaults()
    {
        var service = CreateService();
        Place(service, "001", "S", "P", "S", "R1", "R2"); // route wants Reverse; switch stays Normal
        long sequence = Drive(service, "001", 0, 149, 1);

        var result = service.ApplyTrainPosition(Report("001", 151, sequence + 1));

        Assert.AreEqual(TrainPositionOutcome.Faulted, result.Outcome, result.Detail);
        Assert.Contains("sends the train to N1", result.Detail);
        Assert.IsTrue(service.FindBlock("N1")!.IsOccupied, "The physical leg the train entered is protected.");
        Assert.IsFalse(service.FindBlock("R1")!.IsOccupied, "The route's leg is not occupied on its behalf.");
        Assert.AreEqual(TrainPositionOutcome.Rejected, service.ApplyTrainPosition(Report("001", 160, sequence + 10)).Outcome);
    }

    [TestMethod]
    public void TrailingSwitch_SetForTheOtherLeg_IsARunThroughFault()
    {
        var service = CreateService();
        Place(service, "001", "S", "N1", "S", "P");
        service.ApplyCommand(new() { BlockId = "S", Switch = SwitchPosition.Reverse }); // S is clear: it can be thrown
        long sequence = Drive(service, "001", 0, 99, 1);

        var result = service.ApplyTrainPosition(Report("001", 101, sequence + 1));

        Assert.AreEqual(TrainPositionOutcome.Faulted, result.Outcome, result.Detail);
        Assert.Contains("run through", result.Detail);
        Assert.IsTrue(service.FindBlock("S")!.IsOccupied);
    }

    [TestMethod]
    public void OccupiedSwitch_CannotBeThrownWhileOnlyTheTrailingPartOfTheTrainIsInIt()
    {
        var service = CreateService();
        Place(service, "001", "S", "P", "S", "N1", "N2");
        Drive(service, "001", 0, 160, 1); // front 10 m into N1, rear 127.8 m: still in S

        CollectionAssert.AreEqual(new[] { "S", "N1" }, Occupied(service));
        Assert.Throws<InvalidOperationException>(() => service.ApplyCommand(new() { BlockId = "S", Switch = SwitchPosition.Reverse }));
    }

    [TestMethod]
    public void RollingBack_ReoccupiesTheBlockBehind_AndForwardAgainClearsIt()
    {
        var service = Placed();
        long sequence = Drive(service, "001", 0, 140, 1);
        CollectionAssert.AreEqual(new[] { "B" }, Occupied(service));

        // Rolls back 10 m: the rear (97.8 m) is back in A.
        Assert.AreEqual(TrainPositionOutcome.Applied, service.ApplyTrainPosition(Report("001", 130, sequence + 20, speed: -0.5)).Outcome);
        CollectionAssert.AreEqual(new[] { "A", "B" }, Occupied(service));
        Assert.AreEqual("B", service.FindTrackedTrain("001")!.HeadBlockId);

        Assert.AreEqual(TrainPositionOutcome.Applied, service.ApplyTrainPosition(Report("001", 140, sequence + 40)).Outcome);
        CollectionAssert.AreEqual(new[] { "B" }, Occupied(service));
    }

    [TestMethod]
    public void RouteUpdate_KeepsTheTraversedDistance_AndReplacesOnlyTheRouteAhead()
    {
        var service = CreateService();
        service.ApplyCommand(new() { BlockId = "S", Switch = SwitchPosition.Reverse });
        Place(service, "001", "S", "P", "S", "N1"); // originally planned via N1
        long sequence = Drive(service, "001", 0, 120, 1);

        // CTC re-plans from P (where it still places the train) via R1; the train's front is already in S.
        var update = service.UpdateTrainRoute(new TrainRouteUpdateMessage { RequestId = "u1", TrainId = "001", LineId = "S", RouteBlockIds = ["P", "S", "R1", "R2"] });
        Assert.AreEqual(TrainPlacementOutcome.Registered, update.Outcome, update.Detail);

        Drive(service, "001", 120, 240, sequence);
        var tracked = service.FindTrackedTrain("001")!;
        Assert.AreEqual(TrainTrackingStatus.Active, tracked.Status);
        CollectionAssert.AreEqual(new[] { "P", "S", "R1" }, tracked.TraversedPath.ToArray(), "Traversed blocks are never reinterpreted.");
        CollectionAssert.AreEqual(new[] { "R1" }, Occupied(service));
        Assert.AreEqual(240, tracked.FrontPositionMeters, "The cumulative position was never reset.");

        Assert.AreEqual(TrainPlacementOutcome.Rejected, service.UpdateTrainRoute(new TrainRouteUpdateMessage { RequestId = "u2", TrainId = "001", LineId = "S", RouteBlockIds = ["N1", "N2"] }).Outcome,
            "A route that does not continue from where the train physically is cannot be applied.");
    }

    [TestMethod]
    public void ManualBlockPlacement_TakesOverAPendingDispatch_ButCannotMoveAPositionTrackedTrain()
    {
        var service = CreateService();
        service.RequestTrainPlacement(Dispatch("001", "T", "A", "B"));
        service.ApplyTrainUpdate(new TrackModelTrainUpdateMessage { TrainId = "001", CurrentBlockId = "A" });
        Assert.IsNull(service.FindTrackedTrain("001"), "The harness placement replaced the pending dispatch.");
        Assert.AreEqual(TrainPlacementOutcome.AlreadyPlaced, service.RequestTrainPlacement(Dispatch("001", "T", "A", "B")).Outcome);

        Place(service, "002", "T", "C", "D", "E");
        Assert.Throws<InvalidOperationException>(() => service.ApplyTrainUpdate(new TrackModelTrainUpdateMessage { TrainId = "002", CurrentBlockId = "D" }));
        CollectionAssert.AreEqual(new[] { "A", "C" }, Occupied(service));

        service.ApplyTrainUpdate(new TrackModelTrainUpdateMessage { TrainId = "002", CurrentBlockId = "" });
        CollectionAssert.AreEqual(new[] { "A" }, Occupied(service), "Removing a tracked train clears its footprint.");
    }

    [TestMethod]
    public void StartBlockClosedOrOccupiedBeforeTheInitialReport_WithdrawsThePlacement()
    {
        var service = CreateService();
        service.RequestTrainPlacement(Dispatch("001", "T", "A", "B"));
        service.SetMaintenance("A", MaintenanceState.Closed);

        var result = service.ApplyTrainPosition(Report("001", 0, 1));

        Assert.AreEqual(TrainPositionOutcome.PlacementFailed, result.Outcome);
        Assert.IsEmpty(Occupied(service));
        Assert.IsNull(service.FindTrackedTrain("001"));
    }
}
