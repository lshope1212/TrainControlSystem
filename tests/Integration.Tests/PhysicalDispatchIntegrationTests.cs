using CTC.Core.Models;
using CTC.Core.Scheduling;
using TrackModel.Core.Models;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Integration.Tests;

/// <summary>
/// Physical dispatch through every production module over real pipes, with no TestUI and no manual
/// block placement: CTC -> Track Controller -> Track Model -> Train Model (activation), then the Train
/// Model's initial position report -> Track Model occupancy -> Track Controller -> CTC detection.
/// </summary>
[TestClass]
public sealed class PhysicalDispatchIntegrationTests
{
    private static readonly TimeSpan Departure = new(9, 0, 30);

    /// <summary>Green Line, in its direction of travel, no switches or crossings: 100, 100, 200, 200, 100, 100 m.</summary>
    internal static readonly string[] GreenRoute = ["GREEN-63", "GREEN-64", "GREEN-65", "GREEN-66", "GREEN-67", "GREEN-68"];

    /// <summary>Red Line (bidirectional) from its yard entry RED-9 toward block 1, no switches or crossings.</summary>
    internal static readonly string[] RedRoute = ["RED-9", "RED-8", "RED-7", "RED-6", "RED-5", "RED-4"];

    /// <summary>Blue Line from its yard end, before its switch.</summary>
    internal static readonly string[] BlueRoute = ["1", "2", "3", "4"];

    private sealed record TrainRun(string TrainId, string Line, string[] Route, double Seconds = 120);

    /// <summary>The course network with the Track Controller (paused, as in production), CTC and the Track Model; every line reports clear.</summary>
    private static async Task<LiveSystem> StartAsync(TimeSpan? retryInterval = null, params string[] lines)
    {
        var system = new LiveSystem();
        await system.StartTrackControllerAsync(LiveSystem.CourseDeployment);
        await system.StartCtcAsync(retryInterval);
        await system.StartTrackModelAsync(allLines: true);
        await LiveSystem.UntilAsync(() => system.OnCtc(ctc => lines.All(line => ctc.State.FindLine(line) is not null)), "CTC loads the layout.");
        await LiveSystem.UntilAsync(() => system.OnCtc(ctc => lines.SelectMany(line => ctc.State.FindLine(line)!.Blocks).All(block => block.Occupancy == OccupancyState.Clear)), "the lines report clear.");
        return system;
    }

    private static Task Schedule(LiveSystem system, params TrainRun[] runs) => system.OnCtc(ctc =>
    {
        ctc.QueueSchedule(runs.Select(run =>
        {
            var scheduled = new ScheduledTrain { TrainId = run.TrainId, LineId = run.Line };
            for (int i = 0; i < run.Route.Length; i++)
            {
                scheduled.Route.Add(new ScheduledRouteBlock { BlockId = run.Route[i], ArrivalTime = i == 0 ? Departure : i == run.Route.Length - 1 ? Departure + TimeSpan.FromSeconds(run.Seconds) : null });
            }

            return scheduled;
        }).ToList());
        return 0;
    });

    private static Task<DispatchedTrainState?> Dispatched(LiveSystem system, string trainId) => system.OnCtc(ctc => ctc.State.FindDispatchedTrain(trainId));

    private static Task Release(LiveSystem system) => system.SendToCtcAsync(new SystemTimeMessage { SystemTime = Departure, SpeedMultiplier = 1, IsPaused = true });

    private static async Task<bool> DetectedAsync(LiveSystem system, string trainId) =>
        (await Dispatched(system, trainId)) is { ReleaseStatus: TrainReleaseStatus.DetectedOnTrack, MovementRequest.Delivery: MovementRequestDelivery.Delivered };

    [TestMethod]
    public async Task CtcDispatch_ActivatesTheTrainModel_OccupiesTheStartBlock_AndCtcStartsMovementAuthorization()
    {
        await using var system = await StartAsync(null, "Green");
        await using var trainModel = await LiveTrainModel.StartAsync(system);
        await Schedule(system, new TrainRun("001", "Green", GreenRoute));
        var stages = new List<TrainDispatchStage>();

        // The shared clock stays PAUSED: placement must not depend on physics ticks.
        await Release(system);
        await LiveSystem.UntilAsync(async () =>
            {
                if ((await Dispatched(system, "001"))?.DispatchRequest?.Stage is { } stage && (stages.Count == 0 || stages[^1] != stage)) stages.Add(stage);
                return await DetectedAsync(system, "001");
            },
            "CTC detects train 001 at GREEN-63 from occupancy and requests its first movement.", TimeSpan.FromSeconds(15));

        // CTC talked only to the Track Controller, and only with dispatch and movement contracts.
        var sent = system.CtcSent!.Delivered.ToArray();
        var dispatch = sent.OfType<TrainDispatchRequestMessage>().First();
        Assert.AreEqual("001", dispatch.TrainId);
        Assert.AreEqual("Green", dispatch.LineId);
        Assert.AreEqual("GREEN-63", dispatch.StartBlockId);
        CollectionAssert.AreEqual(GreenRoute, dispatch.RouteBlockIds.ToArray());
        Assert.HasCount(1, sent.OfType<TrainDispatchRequestMessage>().Select(m => m.RequestId).Distinct().ToList(), "One placement, however often it was sent.");
        Assert.IsTrue(sent.All(m => m is TrainDispatchRequestMessage or MovementSuggestionMessage), string.Join(", ", sent.Select(m => m.GetType().Name).Distinct()));

        // The Train Model created and activated the train at cumulative position 0 on the Green Line.
        var state = await trainModel.StateAsync("001");
        Assert.IsNotNull(state, "The Train Model created the train.");
        Assert.AreEqual(0d, state.PositionMeters);
        Assert.AreEqual(0d, state.VelocityMetersPerSecond);
        Assert.AreEqual("Green Line", state.LineName);
        Assert.AreEqual(32.2, state.TrainLengthMeters, 1e-9, "Its TrainSpecs length is kept.");
        CollectionAssert.AreEqual(new[] { "001" }, await trainModel.RosterAsync());

        // The Track Model occupies exactly the start block, from the Train Model's report.
        var tracked = await system.OnTrackModel(track => track.FindTrackedTrain("001"));
        Assert.AreEqual(TrainTrackingStatus.Active, tracked!.Status);
        CollectionAssert.AreEqual(new[] { "GREEN-63" }, tracked.OccupiedBlockIds.ToArray());
        CollectionAssert.AreEqual(new[] { "GREEN-63" }, await system.OnTrackModel(track => track.Layout.Blocks.Where(b => b.IsOccupied).Select(b => b.Id).ToArray()));

        // Every stage was reported, in order, and CTC's movement request is the normal first one.
        await LiveSystem.UntilAsync(async () => (await Dispatched(system, "001"))!.DispatchRequest!.Stage == TrainDispatchStage.Placed, "the placement is reported.");
        Console.WriteLine("Stages seen at CTC: " + string.Join(" -> ", stages));
        var train = (await Dispatched(system, "001"))!;
        Assert.AreEqual("GREEN-63", train.MovementRequest!.Plan.TargetBlockId);
        Assert.IsNull(train.MovementRequest.Plan.EntryBlockId);
    }

    /// <summary>
    /// The Schedule Builder path end to end: the template is generated from the layout CTC received
    /// from the production Track Model, the Green Line's only yard entry (GREEN-63) is the train's
    /// starting block, and that block is where every module places the train, through the existing
    /// dispatch contracts only.
    /// </summary>
    [TestMethod]
    public async Task ScheduleBuilderTemplate_StartsAtTheYardEntry_AndEveryModulePlacesTheTrainThere()
    {
        await using var system = await StartAsync(null, "Green");
        await using var trainModel = await LiveTrainModel.StartAsync(system);
        var (start, route) = await system.OnCtc(ctc =>
        {
            var template = ScheduleTemplateFactory.Create(ctc.State.FindLine("Green")!, TrainIds.Allocate(ctc.State, 1));
            template.Rows.Single(row => row.BlockId == "GREEN-63").SetTrainTime(0, "09:00:30");
            template.Rows.Single(row => row.BlockId == "GREEN-68").SetTrainTime(0, "09:02:30");
            var result = ScheduleTemplateConverter.Convert(template);
            Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
            ctc.QueueSchedule(result.Trains);
            return (template.GetStartBlockId(0), result.Trains[0].Route.Select(block => block.BlockId).ToArray());
        });
        Assert.AreEqual("GREEN-63", start, "The only Green yard entry is preselected; not GREEN-1.");
        CollectionAssert.AreEqual(GreenRoute, route);
        Assert.IsTrue(await system.OnTrackModel(track => !track.Layout.Blocks.Any(b => b.IsOccupied)), "Scheduling occupies nothing.");

        await Release(system);
        await LiveSystem.UntilAsync(() => DetectedAsync(system, "001"), "CTC detects train 001 at its starting block and requests its first movement.", TimeSpan.FromSeconds(15));

        // CTC -> Track Controller: the existing request, its start the selected block.
        var dispatch = system.CtcSent!.Delivered.OfType<TrainDispatchRequestMessage>().First();
        Assert.AreEqual("GREEN-63", dispatch.StartBlockId);
        Assert.AreEqual("GREEN-63", dispatch.RouteBlockIds[0]);

        // Track Controller -> Track Model -> Train Model: activated at position 0, which occupies the start block.
        var state = await trainModel.StateAsync("001");
        Assert.AreEqual(0d, state!.PositionMeters);
        Assert.AreEqual("Green Line", state.LineName);
        CollectionAssert.AreEqual(new[] { "GREEN-63" }, await system.OnTrackModel(track => track.Layout.Blocks.Where(b => b.IsOccupied).Select(b => b.Id).ToArray()));

        // Occupancy -> Track Controller -> CTC: normal movement authorization begins at the start block.
        var train = (await Dispatched(system, "001"))!;
        Assert.AreEqual("GREEN-63", train.CurrentBlockId);
        Assert.AreEqual("GREEN-63", train.MovementRequest!.Plan.TargetBlockId);
        CollectionAssert.AreEqual(GreenRoute.Skip(1).Take(train.MovementRequest.Plan.PathBlockIds.Count).ToArray(), train.MovementRequest.Plan.PathBlockIds.ToArray());
    }

    [TestMethod]
    public async Task RepeatedDispatchRequests_NeverDuplicateOrResetTheTrain()
    {
        await using var system = await StartAsync(null, "Green");
        await using var trainModel = await LiveTrainModel.StartAsync(system);
        await Schedule(system, new TrainRun("001", "Green", GreenRoute));
        await Release(system);
        await LiveSystem.UntilAsync(() => DetectedAsync(system, "001"), "train 001 is placed and detected.", TimeSpan.FromSeconds(15));
        var dispatch = system.CtcSent!.Delivered.OfType<TrainDispatchRequestMessage>().First();

        // The same request again (a retry), three times, and a different dispatch of the same train.
        for (int i = 0; i < 3; i++)
        {
            await system.SendToTrackControllerAsync(dispatch);
        }

        await system.SendToTrackControllerAsync(new TrainDispatchRequestMessage { RequestId = "another-dispatch", TrainId = "001", LineId = "Green", StartBlockId = "GREEN-63", RouteBlockIds = [.. GreenRoute] });
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        CollectionAssert.AreEqual(new[] { "001" }, await trainModel.RosterAsync(), "Still one train.");
        var tracked = await system.OnTrackModel(track => track.TrackedTrains);
        Assert.HasCount(1, tracked);
        Assert.AreEqual(dispatch.RequestId, tracked[0].DispatchRequestId, "The operating train was not replaced.");
        Assert.AreEqual(TrainTrackingStatus.Active, tracked[0].Status);
        Assert.AreEqual(0d, (await trainModel.StateAsync("001"))!.PositionMeters);
    }

    [TestMethod]
    public async Task TrainModelUnavailable_TheDispatchIsRefusedAsRetryable_AndCompletesOnceItStarts()
    {
        await using var system = await StartAsync(TimeSpan.FromSeconds(1), "Green");
        await Schedule(system, new TrainRun("001", "Green", GreenRoute));

        await Release(system);
        await LiveSystem.UntilAsync(async () => (await Dispatched(system, "001"))?.DispatchRequest is { Stage: TrainDispatchStage.Rejected, IsRetryable: true },
            "the Track Model reports the undeliverable activation to CTC as a retryable refusal.", TimeSpan.FromSeconds(10));
        var refused = (await Dispatched(system, "001"))!;
        Console.WriteLine("While the Train Model is down: " + refused.DispatchRequest!.StageDetail);
        Assert.AreEqual(TrainReleaseStatus.AwaitingPlacement, refused.ReleaseStatus);
        Assert.IsTrue(await system.OnTrackModel(track => track.Layout.Blocks.All(b => !b.IsOccupied)), "Nothing was placed.");

        await using var trainModel = await LiveTrainModel.StartAsync(system);
        await LiveSystem.UntilAsync(() => DetectedAsync(system, "001"), "CTC's retry places the train once the Train Model runs.", TimeSpan.FromSeconds(15));
        CollectionAssert.AreEqual(new[] { "001" }, await trainModel.RosterAsync());
        Assert.HasCount(1, system.CtcSent!.Delivered.OfType<TrainDispatchRequestMessage>().Select(m => m.RequestId).Distinct().ToList(), "Retries reuse the request ID.");
    }

    [TestMethod]
    public async Task TrackModelUnavailable_TheDispatchIsKept_AndCompletesWhenTheTrackModelReturns()
    {
        await using var system = await StartAsync(TimeSpan.FromSeconds(1), "Green");
        await using var trainModel = await LiveTrainModel.StartAsync(system);
        await Schedule(system, new TrainRun("001", "Green", GreenRoute));

        // The Track Model stops just before the departure; CTC still has fresh clear statuses and releases.
        await system.StopTrackModelAsync();
        await Release(system);
        await LiveSystem.UntilAsync(async () => (await Dispatched(system, "001"))?.DispatchRequest is { Stage: TrainDispatchStage.Rejected, IsRetryable: true },
            "the Track Controller reports that the Track Model is unavailable.", TimeSpan.FromSeconds(10));
        Console.WriteLine("While the Track Model is down: " + (await Dispatched(system, "001"))!.DispatchRequest!.StageDetail);
        Assert.HasCount(1, await system.OnCtc(ctc => ctc.State.DispatchedTrains.ToList()), "The dispatch is kept, not lost or duplicated.");

        await system.StartTrackModelAsync(allLines: true);
        await LiveSystem.UntilAsync(() => DetectedAsync(system, "001"), "the retried dispatch places the train when the Track Model returns.", TimeSpan.FromSeconds(20));
        CollectionAssert.AreEqual(new[] { "001" }, await trainModel.RosterAsync());
    }

    [TestMethod]
    public async Task ThreeTrains_OnAllThreeLines_AreDispatchedAndDetectedIndependently()
    {
        await using var system = await StartAsync(null, "Green", "Red", "Blue");
        await using var trainModel = await LiveTrainModel.StartAsync(system);
        await Schedule(system, new TrainRun("001", "Green", GreenRoute), new TrainRun("002", "Red", RedRoute), new TrainRun("003", "Blue", BlueRoute, 60));

        await Release(system);
        await LiveSystem.UntilAsync(async () => await DetectedAsync(system, "001") && await DetectedAsync(system, "002") && await DetectedAsync(system, "003"),
            "all three trains are placed and detected.", TimeSpan.FromSeconds(20));

        CollectionAssert.AreEquivalent(new[] { "001", "002", "003" }, await trainModel.RosterAsync());
        Assert.AreEqual("Green Line", (await trainModel.StateAsync("001"))!.LineName);
        Assert.AreEqual("Red Line", (await trainModel.StateAsync("002"))!.LineName);
        Assert.AreEqual("Blue Line", (await trainModel.StateAsync("003"))!.LineName);
        CollectionAssert.AreEquivalent(new[] { "GREEN-63", "RED-9", "1" }, await system.OnTrackModel(track => track.Layout.Blocks.Where(b => b.IsOccupied).Select(b => b.Id).ToArray()));
        Assert.AreEqual("002", await system.OnTrackModel(track => track.FindBlock("RED-9")!.TrainId));
        Assert.AreEqual("003", await system.OnTrackModel(track => track.FindBlock("1")!.TrainId));
        Assert.HasCount(3, system.CtcSent!.Delivered.OfType<TrainDispatchRequestMessage>().Select(m => m.RequestId).Distinct().ToList());
    }
}
