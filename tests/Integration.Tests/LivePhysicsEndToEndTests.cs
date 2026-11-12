using CTC.Core.Models;
using TrackModel.Core.Models;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Integration.Tests;

/// <summary>
/// The whole loop with real physics and no simulated block transitions: CTC dispatches Train 001 on the
/// Green Line; the Track Controller forwards it; the Track Model has the production Train Model activate
/// it at position 0 and occupies the start block from its report; CTC detects it and requests movement;
/// the Track Controller grants authority; the production Train Controller commands traction from the
/// Train Model's status; the Train Model's physics integrates position tick by tick; the Track Model turns
/// that cumulative position into occupancy; and CTC tracks the train along its route from the resulting
/// anonymous occupancy. Nothing here sends a block ID for the train.
/// </summary>
/// <remarks>
/// Every component is the production one over isolated pipes; only the windows (and the Launcher, stood in
/// for by <see cref="ClockPump"/>) are absent. This proves the communication and the physics-to-occupancy
/// chain; it does not prove the WPF windows themselves.
/// </remarks>
[TestClass]
public sealed class LivePhysicsEndToEndTests
{
    private static readonly TimeSpan Departure = new(9, 0, 30);

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Train001_DispatchedByCtc_RunsOnRealPhysics_AndCtcTracksItFromTheResultingOccupancy()
    {
        string[] route = PhysicalDispatchIntegrationTests.GreenRoute;
        await using var system = new LiveSystem();
        await system.StartTrackControllerAsync(LiveSystem.CourseDeployment);
        await system.StartCtcAsync();
        await system.StartTrackModelAsync(allLines: true);
        await using var trainModel = await LiveTrainModel.StartAsync(system);
        await using var trainController = new LiveTrainController(system);

        // The Engineer's step in the Train Controller window: the startup Kp = 1 W per m/s is an explicit
        // untuned placeholder that barely moves a 40 t train. These are the repository's arbitrary test
        // gains (ControllerTestInputs.ArbitraryTestGains), not tuned values.
        trainController.Subsystem.Registry.Get("TRAIN-001").SetEngineerSettings(kp: 20_000, ki: 0);
        await LiveSystem.UntilAsync(() => system.OnCtc(ctc => ctc.State.FindLine("Green") is { } green && green.Blocks.All(block => block.Occupancy == OccupancyState.Clear)), "CTC sees the Green Line clear.");

        await system.OnCtc(ctc =>
        {
            var scheduled = new ScheduledTrain { TrainId = "001", LineId = "Green" };
            for (int i = 0; i < route.Length; i++)
            {
                scheduled.Route.Add(new ScheduledRouteBlock { BlockId = route[i], ArrivalTime = i == 0 ? Departure : i == route.Length - 1 ? Departure + TimeSpan.FromSeconds(110) : null });
            }

            ctc.QueueSchedule([scheduled]);
            return 0;
        });
        await system.ResumeTrackControllerAsync();

        // The shared clock runs at 5x for CTC and the Train Model (the Launcher's role).
        await using var clock = new ClockPump(Departure - TimeSpan.FromSeconds(1), 5, system.CtcPipe, system.TrainModelPipe);

        // 1. CTC dispatches; the Train Model is activated at 0; the Track Model occupies GREEN-63; CTC detects it.
        await LiveSystem.UntilAsync(async () => (await system.OnCtc(ctc => ctc.State.FindDispatchedTrain("001")))?.ReleaseStatus == TrainReleaseStatus.DetectedOnTrack,
            "CTC detects Train 001 at its start block.", TimeSpan.FromSeconds(20));
        var placedAt = await trainModel.StateAsync("001");
        Assert.IsNotNull(placedAt);
        Assert.AreEqual("Green Line", placedAt.LineName);
        Assert.IsLessThan(5.0, placedAt.PositionMeters, "Placed at cumulative position 0 (it may have just started to move).");

        // 2. The Track Controller grants the movement and the Train Controller applies traction.
        await LiveSystem.UntilAsync(async () => (await system.OnCtc(ctc => ctc.State.FindDispatchedTrain("001")!.MovementRequest))?.IsMovementAuthorized == true,
            "the Track Controller grants CTC's movement request.", TimeSpan.FromSeconds(15));
        await LiveSystem.UntilAsync(() => Task.FromResult(trainController.Sink.Sent.Any(command => command.TrainId == "001" && command.PowerCommandWatts > 0)),
            "the Train Controller commands traction.", TimeSpan.FromSeconds(15));

        // 3. Physics moves the train; the Track Model advances occupancy from its cumulative position;
        //    the Track Controller reports it; CTC follows the train through its route.
        var seenAtCtc = new List<string> { "GREEN-63" };
        var footprints = new List<string>();
        string? handoffWhileStraddling = null;
        var nextReport = DateTime.UtcNow;
        await LiveSystem.UntilAsync(async () =>
            {
                var train = await system.OnCtc(ctc => ctc.State.FindDispatchedTrain("001")!);
                string current = train.CurrentBlockId;
                if (seenAtCtc[^1] != current) seenAtCtc.Add(current);
                var tracked = await system.OnTrackModel(track => track.FindTrackedTrain("001"));
                string footprint = string.Join("+", tracked?.OccupiedBlockIds ?? []);
                if (footprints.Count == 0 || footprints[^1] != footprint) footprints.Add(footprint);

                // The front's entry into GREEN-64 promoted the prepared successor while the rear still occupied GREEN-63.
                var entered = system.WaysideBlock("GREEN-64");
                if (handoffWhileStraddling is null && entered.Request?.PredecessorRequestId is { Length: > 0 } && entered.AuthorizedAuthorityMeters > 0
                    && system.WaysideBlock("GREEN-63").Occupancy == OccupancyState.Occupied)
                {
                    handoffWhileStraddling = $"successor {entered.Request.RequestId} replaced {entered.Request.PredecessorRequestId} with {entered.AuthorizedAuthorityMeters:0} m while GREEN-63 was still occupied";
                }

                if (DateTime.UtcNow >= nextReport)
                {
                    nextReport = DateTime.UtcNow.AddSeconds(3);
                    var state = await trainModel.StateAsync("001");
                    var last = trainController.Sink.Sent.LastOrDefault(command => command.TrainId == "001");
                    var request = train.MovementRequest;
                    Console.WriteLine($"[{clock.Now}] TM pos {state?.PositionMeters:0.0} v {state?.VelocityMetersPerSecond:0.0} auth {state?.CommandedAuthorityMeters:0} cmdV {state?.CommandedSpeedMetersPerSecond:0.0} | "
                        + $"Track {tracked?.Status} front {tracked?.FrontPositionMeters:0.0} seq {tracked?.LastSequence} [{footprint}] {tracked?.Fault} | "
                        + $"CTC {current} req {request?.Plan.TargetBlockId} {request?.Authorization} {request?.AuthorizedAuthorityMeters:0} staged {train.StagedMovementRequest?.Plan.TargetBlockId} {train.StagedMovementRequest?.Acknowledgement} | "
                        + $"TC power {last?.PowerCommandWatts:0} sb {last?.ServiceBrakeCommand} eb {last?.EmergencyBrakeCommand}");
                }

                return current == "GREEN-66";
            },
            "CTC tracks Train 001 into GREEN-66 from occupancy produced by its physics.", TimeSpan.FromSeconds(60));

        var model = (await trainModel.StateAsync("001"))!;
        var trackedTrain = (await system.OnTrackModel(track => track.FindTrackedTrain("001")))!;
        Console.WriteLine($"CTC location sequence: {string.Join(" -> ", seenAtCtc)}");
        Console.WriteLine($"Track Model footprints: {string.Join(" | ", footprints)}");
        Console.WriteLine($"Train Model position {model.PositionMeters:0.0} m at {model.VelocityMetersPerSecond:0.0} m/s; Track Model front {trackedTrain.FrontPositionMeters:0.0} m, last sequence {trackedTrain.LastSequence}.");
        string[] log = await trainModel.LogAsync();
        Console.WriteLine("Train Model log: " + string.Join(" | ", log.Take(8)));

        CollectionAssert.AreEqual(route.Take(4).ToArray(), seenAtCtc.ToArray(), "CTC advanced block by block along the route, never skipping one.");
        Assert.AreEqual(TrainTrackingStatus.Active, trackedTrain.Status, trackedTrain.Fault);
        CollectionAssert.AreEqual(route.Take(4).ToArray(), trackedTrain.TraversedPath.Take(4).ToArray());
        Assert.IsGreaterThan(400.0, trackedTrain.FrontPositionMeters, "GREEN-66 starts 400 m from GREEN-63's entry.");
        Assert.IsLessThan(10.0, Math.Abs(model.PositionMeters - trackedTrain.FrontPositionMeters), "The Track Model's front is the Train Model's reported cumulative position.");
        Assert.IsTrue(footprints.Any(footprint => footprint.Contains('+')), "While crossing a boundary the train occupied two blocks at once.");
        Console.WriteLine("Handoff: " + handoffWhileStraddling);
        Assert.IsNotNull(handoffWhileStraddling, "The front entering GREEN-64 handed authority to the prepared successor while GREEN-63 stayed occupied (never reported clear early).");
        Assert.IsTrue(trainController.Sink.Sent.Where(command => command.TrainId == "001").All(command => !command.EmergencyBrakeCommand),
            "Authority passed from block to block without a false zero (no emergency brake).");
    }
}
