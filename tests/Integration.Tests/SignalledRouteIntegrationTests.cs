using CTC.Core.Models;
using TrackModel.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Integration.Tests;

/// <summary>
/// The live Blue Line demonstration through the signalled blocks, with every production component over
/// isolated pipes: CTC dispatches Train 001 from block 1; the Track Controller (course deployment, bundled
/// <c>wayside.plc</c>) prepares the route and commands the protecting signal; the real Track Model applies
/// that command and reports the aspect back; only then is authority granted; it reaches the production
/// Train Model and Train Controller, and the train moves on real physics.
/// </summary>
/// <remarks>
/// Regression for "Withheld: Waiting for proceed signal feedback at block 6", which used to last forever.
/// No test sets a signal itself: every aspect seen here was commanded by the Track Controller and
/// applied by the Track Model.
/// </remarks>
[TestClass]
public sealed class SignalledRouteIntegrationTests
{
    private static readonly TimeSpan Departure = new(9, 0, 30);
    private static readonly string[] ToStationB = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "10"];
    private static readonly string[] ToStationC = ["1", "2", "3", "4", "5", "11", "12", "13", "14", "15"];

    private sealed class Run : IAsyncDisposable
    {
        public required LiveSystem System { get; init; }
        public required LiveTrainModel TrainModel { get; init; }
        public required LiveTrainController TrainController { get; init; }
        public required ClockPump Clock { get; init; }

        public Task<DispatchedTrainState?> Train() => System.OnCtc(ctc => ctc.State.FindDispatchedTrain("001"));

        public async ValueTask DisposeAsync()
        {
            await Clock.DisposeAsync();
            await TrainController.DisposeAsync();
            await TrainModel.DisposeAsync();
            await System.DisposeAsync();
        }
    }

    /// <summary>The demonstration set-up: course (Blue/Red/Green) deployment, all five modules, Train 001 queued on <paramref name="route"/>, the Track Controller running.</summary>
    private static async Task<Run> StartAsync(string[] route, Action<TrackService>? prepareTrack = null)
    {
        var system = new LiveSystem();
        await system.StartTrackControllerAsync(LiveSystem.CourseDeployment);
        await system.StartCtcAsync();
        await system.StartTrackModelAsync(allLines: true, prepare: prepareTrack);
        var trainModel = await LiveTrainModel.StartAsync(system);
        var trainController = new LiveTrainController(system);

        // The repository's arbitrary software-controller test gains (as in LivePhysicsEndToEndTests).
        trainController.Subsystem.Registry.Get("TRAIN-001").SetEngineerSettings(kp: 20_000, ki: 0);
        await LiveSystem.UntilAsync(() => system.OnCtc(ctc => ctc.State.FindLine("Blue") is { } blue && blue.Blocks.All(block => block.Occupancy == OccupancyState.Clear)), "CTC sees the Blue Line clear.");
        await system.OnCtc(ctc =>
        {
            var scheduled = new ScheduledTrain { TrainId = "001", LineId = "Blue" };
            for (int i = 0; i < route.Length; i++)
            {
                scheduled.Route.Add(new ScheduledRouteBlock { BlockId = route[i], ArrivalTime = i == 0 ? Departure : i == route.Length - 1 ? Departure + TimeSpan.FromSeconds(120) : null });
            }

            ctc.QueueSchedule([scheduled]);
            return 0;
        });
        await system.ResumeTrackControllerAsync();
        var clock = new ClockPump(Departure - TimeSpan.FromSeconds(1), 5, system.CtcPipe, system.TrainModelPipe);
        var run = new Run { System = system, TrainModel = trainModel, TrainController = trainController, Clock = clock };
        await LiveSystem.UntilAsync(async () => (await run.Train())?.ReleaseStatus == TrainReleaseStatus.DetectedOnTrack,
            "CTC dispatches Train 001 and detects it in block 1.", TimeSpan.FromSeconds(20));
        Assert.AreEqual("1", (await run.Train())!.CurrentBlockId);
        CollectionAssert.AreEqual(new[] { "1" }, await system.OnTrackModel(track => track.Layout.Blocks.Where(block => block.LineId == "Blue" && block.IsOccupied).Select(block => block.Id).ToArray()));
        return run;
    }

    /// <summary>Waits for the grant, then checks it end to end: real aspect, positive values at the Train Model and Train Controller, and motion.</summary>
    private static async Task AssertSignalClearsAndTrainMovesAsync(Run run, string signal, string otherSignal)
    {
        var system = run.System;
        var reasons = new List<string>();
        await LiveSystem.UntilAsync(async () =>
            {
                var request = (await run.Train())!.MovementRequest;
                string reason = system.WaysideBlock("1").MovementReason;
                if (reasons.Count == 0 || reasons[^1] != reason) reasons.Add(reason);
                return request is { IsMovementAuthorized: true };
            },
            "the Track Controller clears the signal and grants the movement.", TimeSpan.FromSeconds(20));
        Console.WriteLine("Track Controller reasons until the grant: " + string.Join(" -> ", reasons));

        // The aspect that permitted the grant was applied by the Track Model and reported back.
        var protecting = system.WaysideBlock(signal);
        Assert.AreEqual(SignalState.Green, protecting.CommandedSignal);
        Assert.AreEqual(SignalState.Green, protecting.ReportedSignal, "Granted on the Track Model's reported aspect.");
        Assert.AreEqual(SignalState.Green, await system.OnTrackModel(track => track.FindBlock(signal)!.Signal));
        Assert.AreEqual(SignalState.Red, system.WaysideBlock(otherSignal).CommandedSignal, "The branch not taken stays Red.");

        var granted = (await run.Train())!.MovementRequest!;
        Assert.IsGreaterThan(0.0, granted.AuthorizedAuthorityMeters);
        Assert.IsGreaterThan(0.0, granted.AuthorizedSpeedMetersPerSecond);

        // The commanded values reach the Train Model and the Train Controller.
        await LiveSystem.UntilAsync(async () => (await run.TrainModel.StateAsync("001")) is { CommandedAuthorityMeters: > 0, CommandedSpeedMetersPerSecond: > 0 },
            "the Train Model receives positive commanded speed and authority.", TimeSpan.FromSeconds(10));
        await LiveSystem.UntilAsync(() => Task.FromResult(run.TrainController.Subsystem.Registry.Get("TRAIN-001").LastModelInput is { RemainingAuthorityMeters: > 0, CommandedSpeedMetersPerSecond: > 0 }),
            "the Train Controller receives positive commanded speed and authority.", TimeSpan.FromSeconds(10));
        await LiveSystem.UntilAsync(() => Task.FromResult(run.TrainController.Sink.Sent.Any(command => command.TrainId == "001" && command.PowerCommandWatts > 0 && !command.ServiceBrakeCommand && !command.EmergencyBrakeCommand)),
            "the Train Controller releases its brakes and commands traction.", TimeSpan.FromSeconds(10));

        // The train moves on physics; the Track Model, Track Controller and CTC follow it into block 2.
        await LiveSystem.UntilAsync(async () => (await run.Train())!.CurrentBlockId == "2", "CTC tracks Train 001 into block 2.", TimeSpan.FromSeconds(40));
        Assert.AreEqual(OccupancyState.Occupied, system.WaysideBlock("2").Occupancy);
        Assert.IsGreaterThan(0.0, (await run.TrainModel.StateAsync("001"))!.PositionMeters);
    }

    /// <summary>Test A: the live demonstration route, A1 toward Station B (block 10) through signal 6.</summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Train001_ToStationB_ClearsSignal6_IsAuthorized_AndMoves()
    {
        await using var run = await StartAsync(ToStationB);
        await AssertSignalClearsAndTrainMovesAsync(run, signal: "6", otherSignal: "11");

        // On through the switch and past the signal: CTC follows the train block by block into block 7,
        // with authority handed from block to block and no emergency brake on the way.
        var seen = new List<string> { "1", "2" };
        await LiveSystem.UntilAsync(async () =>
            {
                string current = (await run.Train())!.CurrentBlockId;
                if (seen[^1] != current) seen.Add(current);
                return current == "7";
            },
            "CTC tracks Train 001 past signal 6 into block 7.", TimeSpan.FromSeconds(60));
        Console.WriteLine("CTC location sequence: " + string.Join(" -> ", seen));
        CollectionAssert.AreEqual(ToStationB.Take(7).ToArray(), seen.ToArray(), "Block by block, never skipping one.");
        Assert.IsTrue(run.TrainController.Sink.Sent.Where(command => command.TrainId == "001").All(command => !command.EmergencyBrakeCommand),
            "Through the signalled block without a false zero.");
    }

    /// <summary>
    /// Test B: the alternate branch, toward Station C (block 15) through signal 11. Switch 5 must lie
    /// Reverse. The Track Model starts with it Reverse because the Track Controller cannot throw it live:
    /// the Track Model does not report CTC reservation feedback (a separate, known limitation).
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Train001_ToStationC_ClearsSignal11_IsAuthorized_AndMoves()
    {
        await using var run = await StartAsync(ToStationC, track => track.FindBlock("5")!.Switch = SwitchPosition.Reverse);
        await AssertSignalClearsAndTrainMovesAsync(run, signal: "11", otherSignal: "6");
        Assert.AreEqual(SwitchPosition.Reverse, run.System.WaysideBlock("5").ReportedSwitch);
    }

    /// <summary>
    /// Test D: after the grant through signal 6, a broken rail in block 7 (inside the protected route)
    /// withdraws authority at once, signal 6 is commanded Red and the Track Model shows it, the Train Model
    /// receives zero authority, and the Train Controller brakes on its own protections.
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AfterTheGrant_BrokenRailBeyondSignal6_RevokesAuthority_RestrictsTheSignal_AndTheTrainBrakes()
    {
        await using var run = await StartAsync(ToStationB);
        var system = run.System;
        await LiveSystem.UntilAsync(() => Task.FromResult(run.TrainController.Sink.Sent.Any(command => command.TrainId == "001" && command.PowerCommandWatts > 0)),
            "the train is granted through signal 6 and takes traction.", TimeSpan.FromSeconds(25));
        Assert.AreEqual(SignalState.Green, await system.OnTrackModel(track => track.FindBlock("6")!.Signal));

        int sentBefore = run.TrainController.Sink.Sent.Count;
        await system.SendToTrackModelAsync(new TrackModelFailureCommandMessage { BlockId = "7", BrokenRail = true });

        await LiveSystem.UntilAsync(() => Task.FromResult(system.Wayside!.LatestSnapshot.Authorizations.All(block => block.AuthorizedAuthorityMeters == 0)),
            "the Track Controller withdraws the authority.", TimeSpan.FromSeconds(5));
        Assert.AreEqual(SignalState.Red, system.WaysideBlock("6").CommandedSignal, "Signal 6 is re-evaluated and commanded Red.");
        await LiveSystem.UntilAsync(async () => await system.OnTrackModel(track => track.FindBlock("6")!.Signal) == SignalState.Red,
            "the Track Model applies the restrictive aspect.", TimeSpan.FromSeconds(5));
        await LiveSystem.UntilAsync(async () => (await run.TrainModel.StateAsync("001")) is { CommandedAuthorityMeters: 0 },
            "the Train Model receives zero authority.", TimeSpan.FromSeconds(5));
        await LiveSystem.UntilAsync(() => Task.FromResult(run.TrainController.Sink.Sent.Skip(sentBefore).Any(command => command.TrainId == "001" && command.PowerCommandWatts == 0 && (command.ServiceBrakeCommand || command.EmergencyBrakeCommand))),
            "the Train Controller cuts traction and brakes.", TimeSpan.FromSeconds(10));
        Assert.IsFalse((await run.Train())!.MovementRequest!.IsMovementAuthorized, "CTC reports the withdrawal.");
    }
}
