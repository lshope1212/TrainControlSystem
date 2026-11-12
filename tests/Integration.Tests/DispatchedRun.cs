using System.Collections.Concurrent;
using CTC.Core.Models;
using TrackModel.Core.Services;
using TrainControl.Contracts.Enums;

namespace TrainControl.Integration.Tests;

/// <summary>
/// One physically dispatched Train 001 with every production component over isolated pipes: course
/// (Blue/Red/Green) deployment with the bundled PLC, CTC, Track Model, Train Model and Train Controller.
/// CTC schedules the train on <c>route</c>, dispatches it from the route's first block (a yard entry),
/// and the shared clock runs; nothing sets a signal or a switch on the train's behalf.
/// </summary>
internal sealed class DispatchedRun : IAsyncDisposable
{
    public static readonly TimeSpan Departure = new(9, 0, 30);

    public required LiveSystem System { get; init; }
    public required LiveTrainModel TrainModel { get; init; }
    public required LiveTrainController TrainController { get; init; }
    public required ClockPump Clock { get; init; }
    public required string LineId { get; init; }

    /// <summary>
    /// Every switch of the line as the Track Model reported it to the Track Controller after start-up,
    /// before the Track Controller was resumed and the train dispatched.
    /// </summary>
    public required IReadOnlyDictionary<string, SwitchPosition> InitialSwitches { get; init; }

    /// <summary>Every switch of the line, watched from before the Track Controller was resumed.</summary>
    public required SwitchWatch Switches { get; init; }

    public Task<DispatchedTrainState?> Train() => System.OnCtc(ctc => ctc.State.FindDispatchedTrain("001"));

    /// <param name="travelSeconds">Scheduled time from departure to the last route block (sets CTC's suggested speed).</param>
    /// <param name="prepareTrack">The Track Model's start-up state (e.g. another train or a failure); never used to set a switch for the route.</param>
    /// <param name="beforeResume">Runs with every module connected and the Track Controller still paused (e.g. a dispatcher's maintenance closure).</param>
    /// <param name="schedule">
    /// Builds the schedule on CTC's thread instead (e.g. through the Schedule Builder's template, letting CTC route the
    /// train); <paramref name="route"/>[0] is then only the start block it is expected at.
    /// </param>
    /// <param name="clockMultiplier">The shared clock's speed (the Train Model accepts 1 to 10).</param>
    public static async Task<DispatchedRun> StartAsync(string lineId, string[] route, Action<TrackService>? prepareTrack = null, double travelSeconds = 120, Func<LiveSystem, Task>? beforeResume = null,
        Func<CTC.Core.Services.CTCService, IReadOnlyList<ScheduledTrain>>? schedule = null, double clockMultiplier = 5)
    {
        var system = new LiveSystem();
        try
        {
            await system.StartTrackControllerAsync(LiveSystem.CourseDeployment);
            await system.StartCtcAsync();
            await system.StartTrackModelAsync(allLines: true, prepare: prepareTrack);
        }
        catch
        {
            await system.DisposeAsync();
            throw;
        }

        var trainModel = await LiveTrainModel.StartAsync(system);
        var trainController = new LiveTrainController(system);

        // The repository's arbitrary software-controller test gains (as in LivePhysicsEndToEndTests).
        trainController.Subsystem.Registry.Get("TRAIN-001").SetEngineerSettings(kp: 20_000, ki: 0);
        await LiveSystem.UntilAsync(() => system.OnCtc(ctc => ctc.State.FindLine(lineId) is { } line && line.Blocks.Any(block => block.BlockId == route[0] && block.Occupancy == OccupancyState.Clear)),
            $"CTC sees {lineId} block {route[0]} clear.");
        await system.OnCtc(ctc =>
        {
            if (schedule is not null)
            {
                ctc.QueueSchedule(schedule(ctc));
                return 0;
            }

            var scheduled = new ScheduledTrain { TrainId = "001", LineId = lineId };
            for (int i = 0; i < route.Length; i++)
            {
                scheduled.Route.Add(new ScheduledRouteBlock { BlockId = route[i], ArrivalTime = i == 0 ? Departure : i == route.Length - 1 ? Departure + TimeSpan.FromSeconds(travelSeconds) : null });
            }

            ctc.QueueSchedule([scheduled]);
            return 0;
        });

        // The start-up positions, before the (paused) Track Controller may command anything.
        var switchIds = await system.OnTrackModel(track => track.Layout.Blocks.Where(block => block.LineId == lineId && block.HasSwitch).Select(block => block.Id).ToArray());
        await LiveSystem.UntilAsync(() => Task.FromResult(switchIds.All(id => system.WaysideBlock(id).IsFresh)), $"the Track Controller has fresh feedback for every {lineId} switch.");
        var initial = switchIds.ToDictionary(id => id, id => system.WaysideBlock(id).ReportedSwitch);
        foreach (var (id, position) in initial)
        {
            Assert.AreEqual(position, await system.OnTrackModel(track => track.FindBlock(id)!.Switch), $"Switch {id}: the Track Controller sees the Track Model's position.");
        }

        if (beforeResume is not null)
        {
            await beforeResume(system);
        }

        var watch = new SwitchWatch(system, switchIds);
        await system.ResumeTrackControllerAsync();
        var clock = new ClockPump(Departure - TimeSpan.FromSeconds(1), clockMultiplier, system.CtcPipe, system.TrainModelPipe);
        var run = new DispatchedRun { System = system, TrainModel = trainModel, TrainController = trainController, Clock = clock, LineId = lineId, InitialSwitches = initial, Switches = watch };
        await LiveSystem.UntilAsync(async () => (await run.Train())?.ReleaseStatus == TrainReleaseStatus.DetectedOnTrack,
            $"CTC dispatches Train 001 and detects it in block {route[0]}.", TimeSpan.FromSeconds(20));
        Assert.AreEqual(route[0], (await run.Train())!.CurrentBlockId);
        CollectionAssert.Contains(await system.OnTrackModel(track => track.Layout.Blocks.Where(block => block.LineId == lineId && block.IsOccupied).Select(block => block.Id).ToArray()), route[0]);
        return run;
    }

    /// <summary>Follows CTC's location of the train until it reaches <paramref name="blockId"/>, returning every block it was tracked in.</summary>
    public async Task<List<string>> FollowUntilAsync(string blockId, TimeSpan timeout, List<string>? seen = null)
    {
        seen ??= [(await Train())!.CurrentBlockId];
        await LiveSystem.UntilAsync(async () =>
            {
                string current = (await Train())!.CurrentBlockId;
                if (seen.Count == 0 || seen[^1] != current) seen.Add(current);
                return current == blockId;
            },
            $"CTC tracks Train 001 into block {blockId}.", timeout);
        return seen;
    }

    public async ValueTask DisposeAsync()
    {
        await Switches.DisposeAsync();
        await Clock.DisposeAsync();
        await TrainController.DisposeAsync();
        await TrainModel.DisposeAsync();
        await System.DisposeAsync();
    }
}

/// <summary>
/// Samples, every 25 ms, what the Track Controller commands and what the Track Model physically has for
/// a set of switches, recording each distinct value in order. Used to prove that a switch moved (or did
/// not), and that it never oscillated.
/// </summary>
internal sealed class SwitchWatch : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, List<SwitchPosition>> _commanded = new(), _physical = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;

    public SwitchWatch(LiveSystem system, params string[] switchIds)
    {
        foreach (string id in switchIds)
        {
            _commanded[id] = [];
            _physical[id] = [];
        }

        _run = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var snapshot = system.Wayside!.LatestSnapshot;
                    var physical = await system.OnTrackModel(track => switchIds.ToDictionary(id => id, id => track.FindBlock(id)!.Switch));
                    foreach (string id in switchIds)
                    {
                        Record(_commanded[id], snapshot.Blocks.Single(block => block.BlockId == id).CommandedSwitch);
                        Record(_physical[id], physical[id]);
                    }

                    await Task.Delay(25, _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
    }

    private static void Record(List<SwitchPosition> history, SwitchPosition value)
    {
        lock (history)
        {
            if (value is SwitchPosition.Normal or SwitchPosition.Reverse && (history.Count == 0 || history[^1] != value))
            {
                history.Add(value);
            }
        }
    }

    /// <summary>The distinct positions the Track Controller commanded, in order.</summary>
    public SwitchPosition[] Commanded(string id)
    {
        lock (_commanded[id]) return [.. _commanded[id]];
    }

    /// <summary>The distinct positions the Track Model's switch actually took, in order.</summary>
    public SwitchPosition[] Physical(string id)
    {
        lock (_physical[id]) return [.. _physical[id]];
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _run;
        }
        catch (Exception)
        {
            // Sampling stops with the system it watched.
        }

        _stop.Dispose();
    }
}
