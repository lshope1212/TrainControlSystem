using CTC.Core.Models;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Integration.Tests;

/// <summary>
/// Movement authority continuity end to end, through every production module over real pipes:
/// CTC → Track Controller → Track Model → Train Model track-circuit pickup → Train Controller.
/// A valid movement crosses block boundaries without any zero reaching the train; a genuine
/// revocation reaches it at once and the Train Controller's existing protection brakes.
/// </summary>
[TestClass]
public sealed class AuthorityContinuityIntegrationTests
{
    private static readonly TimeSpan Departure = new TimeSpan(9, 0, 30);

    private sealed record TrainRun(string TrainId, string Line, string[] Route);

    /// <summary>
    /// All modules up, the demonstration train removed, <paramref name="trains"/> scheduled, released,
    /// placed in their start blocks through the Track Model, and the Track Controller resumed.
    /// </summary>
    private static async Task<LiveSystem> StartAsync(bool allLines, Func<LiveSystem, string>? deployment = null, params TrainRun[] trains)
    {
        var system = new LiveSystem();
        await system.StartTrackControllerAsync(deployment?.Invoke(system) ?? (allLines ? LiveSystem.CourseDeployment : null));
        await system.StartCtcAsync();
        await system.StartTrackModelAsync(allLines);
        await LiveSystem.UntilAsync(() => system.OnCtc(ctc => trains.All(train => ctc.State.FindLine(train.Line) is not null)), "CTC loads the layout.");

        await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = "01", CurrentBlockId = "" });
        await LiveSystem.UntilAsync(() => system.OnCtc(ctc => trains.SelectMany(train => ctc.State.FindLine(train.Line)!.Blocks).All(block => block.Occupancy == OccupancyState.Clear)), "the lines report clear.");

        await system.OnCtc(ctc =>
        {
            ctc.QueueSchedule(trains.Select(run =>
            {
                var scheduled = new ScheduledTrain { TrainId = run.TrainId, LineId = run.Line };
                for (int i = 0; i < run.Route.Length; i++)
                {
                    scheduled.Route.Add(new ScheduledRouteBlock { BlockId = run.Route[i], ArrivalTime = i == 0 || i == run.Route.Length - 1 ? Departure + TimeSpan.FromSeconds(30 * i) : null });
                }

                return scheduled;
            }).ToList());
            return 0;
        });
        await system.SendToCtcAsync(new SystemTimeMessage { SystemTime = Departure, SpeedMultiplier = 1, IsPaused = true });
        foreach (var run in trains)
        {
            await LiveSystem.UntilAsync(() => system.OnCtc(ctc => ctc.State.FindDispatchedTrain(run.TrainId)?.ReleaseStatus == TrainReleaseStatus.AwaitingPlacement), $"{run.TrainId} is released.");
            await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = run.TrainId, CurrentBlockId = run.Route[0] });
            await LiveSystem.UntilAsync(() => system.OnCtc(ctc => ctc.State.FindDispatchedTrain(run.TrainId)!.MovementRequest is { Delivery: MovementRequestDelivery.Delivered }), $"CTC detects {run.TrainId} and requests its movement.");
        }

        await system.ResumeTrackControllerAsync();
        return system;
    }

    private static Task<DispatchedTrainState> Dispatched(LiveSystem system, string trainId) => system.OnCtc(ctc => ctc.State.FindDispatchedTrain(trainId)!);

    /// <summary>
    /// Waits until the train's request is granted into its next block and the successor is prepared
    /// everywhere: acknowledged at CTC as prepared (not granted), and coded onto the next block in the
    /// Track Model as the granted request's continuation.
    /// </summary>
    private static async Task<(MovementRequestState Active, MovementRequestState Staged)> PreparedAsync(LiveSystem system, TrainRun run)
    {
        await LiveSystem.UntilAsync(async () =>
            {
                var train = await Dispatched(system, run.TrainId);
                return train.MovementRequest is { IsMovementAuthorized: true } active
                    && train.StagedMovementRequest is { Acknowledgement: RequestAcknowledgementOutcome.PreparedAsSuccessor }
                    && await system.OnTrackModel(track => track.FindBlock(run.Route[1])!.MovementRequestId == active.RequestId && track.FindBlock(run.Route[1])!.AuthorityEntryBlockId == run.Route[0]);
            },
            $"{run.TrainId}'s grant reaches {run.Route[1]} and its successor is prepared.", TimeSpan.FromSeconds(15));
        var prepared = await Dispatched(system, run.TrainId);
        Assert.IsFalse(prepared.StagedMovementRequest!.IsMovementAuthorized, "Prepared is never a grant.");
        return (prepared.MovementRequest!, prepared.StagedMovementRequest!);
    }

    /// <summary>The handoff is complete at CTC and the Track Controller, and the train holds the successor's grant.</summary>
    private static async Task<bool> HandedOverAsync(LiveSystem system, TrainRun run, MovementRequestState staged, OnboardTrains.Train train, double expected)
    {
        var dispatched = await Dispatched(system, run.TrainId);
        var target = system.WaysideBlock(run.Route[1]);
        return dispatched.MovementRequest?.RequestId == staged.RequestId
            && target.Request?.RequestId == staged.RequestId && target.AuthorizedAuthorityMeters > 0
            && system.WaysideBlock(run.Route[0]).Request is null
            && Math.Abs(train.Authority - expected) < 1e-6
            && train.Received.LastOrDefault()?.RequestId == staged.RequestId;
    }

    private static void AssertNoZeroSince(OnboardTrains.Train train, int fromPickup, string because)
    {
        var pickups = train.Received.ToArray().Skip(fromPickup).ToList();
        Assert.IsNotEmpty(pickups);
        Assert.IsTrue(pickups.All(pickup => pickup.Authority > 0 && pickup.Speed > 0),
            $"{because} Track-circuit values received: {string.Join(", ", pickups.Select(pickup => $"{pickup.BlockId}:{pickup.Authority:0.#}"))}");
    }

    [TestMethod]
    public async Task MovingTrain_CrossesABlockBoundaryWithoutAFalseZero_AndAGenuineRevocationStopsItAtOnce()
    {
        var run = new TrainRun("001", "Blue", ["1", "2", "3", "4"]);
        await using var system = await StartAsync(false, null, run);
        await using var onboard = new OnboardTrains(system.TrainModelPipe, run.TrainId);
        var train = onboard[run.TrainId];

        var (active, staged) = await PreparedAsync(system, run);
        await LiveSystem.UntilAsync(() => Task.FromResult(Math.Abs(train.Authority - active.AuthorizedAuthorityMeters) < 1e-6), "the Train Model picks up the granted authority.");
        int fromPickup = train.Received.Count - 1;
        double firstBlock = await system.OnTrackModel(track => track.FindBlock("1")!.LengthMeters);
        double expected = active.AuthorizedAuthorityMeters - firstBlock;
        Assert.AreEqual(expected, staged.Plan.AuthorityMeters, 1e-6, "The successor keeps the movement's fixed end point.");

        // The train is moving under the Train Controller's control.
        train.SpeedMetersPerSecond = 8;
        for (int i = 0; i < 5; i++)
        {
            await onboard.TickAsync(run.TrainId);
        }

        // It crosses into block 2 while the controller keeps running.
        await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = run.TrainId, CurrentBlockId = "2", ActualSpeedMetersPerSecond = train.SpeedMetersPerSecond });
        await LiveSystem.UntilAsync(async () =>
            {
                await onboard.TickAsync(run.TrainId);
                return await HandedOverAsync(system, run, staged, train, expected);
            },
            "authority passes to the successor at CTC, the Track Controller and the train.");
        for (int i = 0; i < 5; i++)
        {
            await onboard.TickAsync(run.TrainId);
        }

        AssertNoZeroSince(train, fromPickup, "A valid handoff never delivers a zero to the moving train.");
        var inBlock2 = train.Received.ToArray().Skip(fromPickup).Where(pickup => pickup.BlockId == "2").ToList();
        Assert.AreEqual(expected, inBlock2[0].Authority, 1e-6, "From the moment it enters, the train holds the same end point.");
        Assert.IsTrue(inBlock2.All(pickup => pickup.Authority <= expected + 1e-6), "Never more than the wayside granted.");
        Assert.IsFalse(train.Ticks.Any(tick => tick.EmergencyBrake), "No emergency brake from replacing the request: " + string.Join(" | ", train.Ticks.SelectMany(tick => tick.Alerts).Distinct()));
        Assert.IsNull(system.WaysideBlock("1").Request, "The predecessor was retired by the transfer.");
        Assert.HasCount(1, system.Wayside!.LatestSnapshot.Authorizations.Where(block => block.AuthorizedAuthorityMeters > 0).ToList(), "One grant, never two.");

        // A genuine revocation: the Track Controller withholds all authority.
        var revokedAt = DateTime.UtcNow;
        await system.SendToTrackControllerAsync(MessageSerializer.CreateEnvelope("SetSimulation", new { paused = true, speedMultiplier = 1 }));
        await LiveSystem.UntilAsync(() => Task.FromResult(train.Authority == 0), "the revocation reaches the train.", TimeSpan.FromSeconds(3));
        var zero = train.Received.ToArray().First(pickup => pickup.At >= revokedAt && pickup.Authority == 0);
        var latency = zero.At - revokedAt;
        Console.WriteLine($"Revocation reached the Train Model {latency.TotalMilliseconds:0} ms after the Track Controller was told to stop.");
        Assert.IsLessThan(TimeSpan.FromMilliseconds(500), latency, "Event-driven: no wait for the periodic 500 ms publication, and no grace period.");

        var command = await onboard.TickAsync(run.TrainId);
        Assert.IsTrue(command.EmergencyBrakeCommand, "The Train Controller's existing authority protection brakes on its next control period.");
    }

    [TestMethod]
    public async Task FaultAheadDuringPreparation_WithdrawsTheGrantAtOnce_AndThePreparedSuccessorNeverOverridesIt()
    {
        var run = new TrainRun("001", "Blue", ["1", "2", "3", "4"]);
        await using var system = await StartAsync(false, null, run);
        await using var onboard = new OnboardTrains(system.TrainModelPipe, run.TrainId);
        var train = onboard[run.TrainId];
        var (active, staged) = await PreparedAsync(system, run);
        await LiveSystem.UntilAsync(() => Task.FromResult(train.Authority > 0), "the train holds its grant.");
        train.SpeedMetersPerSecond = 8;
        await onboard.TickAsync(run.TrainId);

        // A broken rail is reported in block 3, inside the granted route.
        var faultAt = DateTime.UtcNow;
        await system.SendToTrackModelAsync(new TrackModelFailureCommandMessage { BlockId = "3", BrokenRail = true });
        await LiveSystem.UntilAsync(() => Task.FromResult(train.Authority == 0), "the fault withdraws the train's authority.", TimeSpan.FromSeconds(3));
        Console.WriteLine($"Fault to zero at the Train Model: {(train.Received.ToArray().First(pickup => pickup.At >= faultAt && pickup.Authority == 0).At - faultAt).TotalMilliseconds:0} ms.");
        Assert.IsTrue((await onboard.TickAsync(run.TrainId)).EmergencyBrakeCommand);
        Assert.AreEqual(0d, await system.OnTrackModel(track => track.FindBlock("2")!.AuthorityMeters), "The continuation went with the grant.");

        // CTC may now re-plan a shorter movement that ends before the fault, and the wayside may grant
        // that on its own evaluation. The train then still crosses into block 2. Whatever happens, no
        // authority ever reaches into block 3 again, and the successor prepared for the longer route
        // never takes effect.
        int fromPickup = train.Received.Count;
        await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = run.TrainId, CurrentBlockId = "2" });
        await LiveSystem.UntilAsync(() => system.OnCtc(ctc => ctc.State.FindDispatchedTrain(run.TrainId)!.CurrentBlockId == "2"), "CTC follows the train.");
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        double first = await system.OnTrackModel(track => track.FindBlock("1")!.LengthMeters);
        double second = await system.OnTrackModel(track => track.FindBlock("2")!.LengthMeters);
        foreach (var pickup in train.Received.ToArray().Skip(fromPickup))
        {
            double toFault = pickup.BlockId == "1" ? first + second : second;
            Assert.IsLessThanOrEqualTo(toFault + 1e-6, pickup.Authority, $"In block {pickup.BlockId} the authority ends before the broken rail.");
            Assert.AreNotEqual(staged.RequestId, pickup.RequestId, "The successor of the withdrawn grant never reaches the train.");
            Assert.AreNotEqual(active.RequestId, pickup.RequestId, "Nor does the withdrawn grant itself.");
        }

        Assert.IsFalse(system.Wayside!.LatestSnapshot.Authorizations.Any(block => block.Request!.RequestId == staged.RequestId), "Never promoted.");
        Assert.IsFalse(system.Wayside.LatestSnapshot.Authorizations.Any(block => block.Request!.PathBlockIds.Contains("3") && block.AuthorizedAuthorityMeters > 0), "Nothing grants movement into block 3.");
        Assert.AreNotEqual(staged.RequestId, (await Dispatched(system, run.TrainId)).MovementRequest?.RequestId, "The withdrawn successor is not adopted.");
    }

    [TestMethod]
    public async Task ThreeLines_HandOverIndependently_WithoutAFalseZeroOnAnyLine()
    {
        TrainRun[] runs =
        [
            new("001", "Blue", ["1", "2", "3", "4"]),
            new("003", "Red", ["RED-20", "RED-21", "RED-22", "RED-23", "RED-24", "RED-25"]),
            new("005", "Green", ["GREEN-63", "GREEN-64", "GREEN-65", "GREEN-66", "GREEN-67", "GREEN-68"]),
        ];
        await using var system = await StartAsync(true, null, runs);
        await using var onboard = new OnboardTrains(system.TrainModelPipe, runs.Select(run => run.TrainId).ToArray());

        var prepared = new Dictionary<string, (MovementRequestState Active, MovementRequestState Staged, int FromPickup, double Expected)>();
        foreach (var run in runs)
        {
            var (active, staged) = await PreparedAsync(system, run);
            var train = onboard[run.TrainId];
            await LiveSystem.UntilAsync(() => Task.FromResult(Math.Abs(train.Authority - active.AuthorizedAuthorityMeters) < 1e-6), $"{run.TrainId} picks up its grant.");
            double length = await system.OnTrackModel(track => track.FindBlock(run.Route[0])!.LengthMeters);
            prepared[run.TrainId] = (active, staged, train.Received.Count - 1, active.AuthorizedAuthorityMeters - length);
        }

        // All three move at once.
        await Task.WhenAll(runs.Select(run => system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = run.TrainId, CurrentBlockId = run.Route[1] })));
        foreach (var run in runs)
        {
            var (_, staged, fromPickup, expected) = prepared[run.TrainId];
            var train = onboard[run.TrainId];
            await LiveSystem.UntilAsync(() => HandedOverAsync(system, run, staged, train, expected), $"{run.Line}: authority passes to the successor.");
            AssertNoZeroSince(train, fromPickup, $"{run.Line}:");
            StringAssert.Contains(system.Wayside!.GetOwnerDescription(run.Route[1]), run.Line + " Line");
        }

        Assert.HasCount(3, system.Wayside!.LatestSnapshot.Authorizations.Where(block => block.AuthorizedAuthorityMeters > 0).ToList(), "One grant per train.");
    }

    [TestMethod]
    public async Task CtcStops_TheWatchdogWithdrawsTheGrantAndItsSuccessor_SoTheTransitionGetsNoAuthority()
    {
        var run = new TrainRun("001", "Blue", ["1", "2", "3", "4"]);
        // A 3 s request watchdog so the test runs in seconds.
        await using var system = await StartAsync(false, live => Path.Combine(live.CopySamples(path => LiveSystem.EditJson(Path.Combine(path, "blue-line-track-map.json"), json => json["requestTimeoutSeconds"] = 3)), "blue-line-all-pc.deployment.json"), run);
        await using var onboard = new OnboardTrains(system.TrainModelPipe, run.TrainId);
        var train = onboard[run.TrainId];
        await PreparedAsync(system, run);
        await LiveSystem.UntilAsync(() => Task.FromResult(train.Authority > 0), "the train holds its grant.");

        await system.StopCtcAsync();
        await LiveSystem.UntilAsync(() => Task.FromResult(train.Authority == 0), "no renewals: the watchdog withdraws the authority.", TimeSpan.FromSeconds(8));
        Assert.IsNull(system.WaysideBlock("2").Continuation, "The unrenewed successor expired too.");

        await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = run.TrainId, CurrentBlockId = "2" });
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.AreEqual(0d, train.Authority);
        Assert.IsFalse(system.Wayside!.LatestSnapshot.Authorizations.Any(block => block.AuthorizedAuthorityMeters > 0));
    }

    [TestMethod]
    public async Task SilentTrackController_ItsCodesLapseToZeroOnTheTrack()
    {
        var run = new TrainRun("001", "Blue", ["1", "2", "3", "4"]);
        await using var system = await StartAsync(false, null, run);
        await using var onboard = new OnboardTrains(system.TrainModelPipe, run.TrainId);
        var train = onboard[run.TrainId];
        await PreparedAsync(system, run);
        await LiveSystem.UntilAsync(() => Task.FromResult(train.Authority > 0), "the train holds its grant.");

        var stoppedAt = DateTime.UtcNow;
        await system.StopTrackControllerAsync();
        await LiveSystem.UntilAsync(() => Task.FromResult(train.Authority == 0), "the lease lapses: the Track Model never keeps an unrenewed authority.", TimeSpan.FromSeconds(4));
        Console.WriteLine($"Track Controller gone to zero at the Train Model: {(DateTime.UtcNow - stoppedAt).TotalMilliseconds:0} ms.");
        Assert.AreEqual(0d, await system.OnTrackModel(track => track.FindBlock("2")!.AuthorityMeters));
    }

    [TestMethod]
    public async Task UnavailableTrainModel_DoesNotHoldUpTheHandoff_AndReceivesTheCurrentCodeWhenItReturns()
    {
        var run = new TrainRun("001", "Blue", ["1", "2", "3", "4"]);
        await using var system = await StartAsync(false, null, run);
        var (active, staged) = await PreparedAsync(system, run);
        double granted = active.AuthorizedAuthorityMeters;

        // No Train Model is listening while the train crosses the boundary.
        await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = run.TrainId, CurrentBlockId = "2" });
        await LiveSystem.UntilAsync(async () => (await Dispatched(system, run.TrainId)).MovementRequest?.RequestId == staged.RequestId && system.WaysideBlock("2").AuthorizedAuthorityMeters > 0,
            "the handoff completes without the Train Model.");

        // The Train Model starts: it gets the train's current code (not an older one) within a heartbeat.
        await using var onboard = new OnboardTrains(system.TrainModelPipe, run.TrainId);
        var train = onboard[run.TrainId];
        double expected = system.WaysideBlock("2").AuthorizedAuthorityMeters;
        await LiveSystem.UntilAsync(() => Task.FromResult(Math.Abs(train.Authority - expected) < 1e-6), "the returning Train Model resynchronizes.", TimeSpan.FromSeconds(4));
        Assert.AreEqual(staged.RequestId, train.Received.Last().RequestId);
        Assert.IsLessThan(granted, train.Authority, "The current code from block 2, not the older one from block 1.");
    }
}
