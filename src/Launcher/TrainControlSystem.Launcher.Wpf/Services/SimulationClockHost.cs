using System.Diagnostics;
using TrainControl.Common.Communication;
using TrainControl.Common.Timing;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControlSystem.Launcher.Wpf.Services;

/// <summary>
/// Hosts the system's ONE authoritative <see cref="SimulationClockService"/> inside the
/// Launcher process. Every module is a separate process, so the clock is shared over the
/// existing named-pipe contracts rather than as an in-memory singleton:
/// <list type="bullet">
///   <item>CTC sends <see cref="SimulationTimeControlMessage"/> requests to
///         <see cref="NamedPipeNames.SimulationClock"/>; nothing else may.</item>
///   <item>Every <see cref="PublishInterval"/> the clock advances by the real time actually
///         elapsed (Stopwatch, so a late timer tick causes no drift) and its state is sent as a
///         <see cref="SystemTimeMessage"/> to each subscribed module pipe, one send per module.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Failure isolation: each module has at most one send in flight. A module that is not running
/// (or is busy) only misses updates; the clock keeps advancing and the other modules keep
/// receiving. Messages carry absolute state, so the next delivered message catches it up.
/// </para>
/// <para>
/// Only one Launcher per Windows session runs the clock (named mutex); a second copy reports
/// that and runs no clock, so there can never be two competing clocks.
/// </para>
/// <para>Events are raised on background threads; subscribers marshal to their UI thread.</para>
/// </remarks>
public sealed class SimulationClockHost
{
    /// <summary>How often clock state is advanced and distributed.</summary>
    public static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(100);

    // Short, so an absent module is detected quickly; it never delays the clock itself.
    private const int SubscriberConnectTimeoutMilliseconds = 250;

    private const string SingleInstanceMutexName = @"Local\TrainControl.SimulationClock";

    private readonly SimulationClockService _clock = new SimulationClockService();
    private readonly Lock _gate = new Lock();
    private readonly IReadOnlyList<Subscriber> _subscribers;
    private CancellationToken _shutdownToken;

    public SimulationClockHost()
    {
        // Modules whose receivers apply SystemTimeMessage. Other modules subscribe by adding
        // their pipe here once their receiver handles it (see the root README).
        _subscribers = new[]
        {
            new Subscriber("CTC", NamedPipeNames.Ctc),
            new Subscriber("Train Model", NamedPipeNames.TrainModel),
        };
    }

    /// <summary>Raised after every tick and control request with the state being distributed.</summary>
    public event EventHandler<SimulationClockState>? StatePublished;

    /// <summary>Raised when a module becomes reachable or unreachable (not on every send).</summary>
    public event EventHandler<ClockDeliveryStatus>? DeliveryStatusChanged;

    /// <summary>Readable notices: accepted/rejected control requests, receive errors.</summary>
    public event EventHandler<string>? StatusReported;

    public SimulationClockState Current
    {
        get
        {
            lock (_gate)
            {
                return _clock.Current;
            }
        }
    }

    public IEnumerable<string> SubscriberNames => _subscribers.Select(subscriber => subscriber.Name);

    /// <summary>Runs the clock until <paramref name="cancellationToken"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var instance = new Mutex(initiallyOwned: false, SingleInstanceMutexName, out bool isOnlyInstance);
        if (!isOnlyInstance)
        {
            Report("Another Launcher is already running the simulation clock; this Launcher runs no clock.");
            return;
        }

        _shutdownToken = cancellationToken;
        Report("Simulation clock started (paused). Control it from the CTC Office.");

        await Task.WhenAll(
            PublishLoopAsync(cancellationToken),
            NamedPipeTransport.ListenAsync(
                NamedPipeNames.SimulationClock,
                HandleControlAsync,
                ex => Report($"Rejected clock request: {ex.Message}"),
                cancellationToken));
    }

    private async Task PublishLoopAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        TimeSpan lastTick = stopwatch.Elapsed;
        using var timer = new PeriodicTimer(PublishInterval);

        Publish();
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // Advance by the real time that actually passed, not the nominal interval.
                TimeSpan now = stopwatch.Elapsed;
                lock (_gate)
                {
                    _clock.Tick(now - lastTick);
                }

                lastTick = now;
                Publish();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private Task HandleControlAsync(MessageEnvelope envelope)
    {
        if (envelope.MessageType != nameof(SimulationTimeControlMessage))
        {
            Report($"Ignored message of type '{envelope.MessageType}' on the simulation clock pipe.");
            return Task.CompletedTask;
        }

        var request = MessageSerializer.DeserializePayload<SimulationTimeControlMessage>(envelope);
        string description;
        lock (_gate)
        {
            // Throws for an invalid request; the listener reports it and the clock is unchanged.
            description = Apply(_clock, request);
        }

        Report($"Clock request applied: {description}.");

        // Distribute the new state now rather than on the next tick.
        Publish();
        if (request.Command == SimulationClockCommand.ResetSystem)
        {
            _ = BroadcastSystemResetAsync();
        }

        return Task.CompletedTask;
    }

    /// <summary>Applies one control request; returns a readable description of it.</summary>
    /// <exception cref="ArgumentException">A required value is missing or out of range.</exception>
    internal static string Apply(ISimulationClockController clock, SimulationTimeControlMessage request)
    {
        switch (request.Command)
        {
            case SimulationClockCommand.Run:
                clock.Run();
                return "run";

            case SimulationClockCommand.Pause:
                clock.Pause();
                return "pause";

            case SimulationClockCommand.SetSpeed:
                double speed = request.SpeedMultiplier ?? throw new ArgumentException("SetSpeed needs a speed multiplier.");
                clock.SetSpeed(speed);
                return $"speed {speed:0.#}x";

            case SimulationClockCommand.SetTime:
                TimeSpan time = request.SystemTime ?? throw new ArgumentException("SetTime needs a system time.");
                clock.SetTime(time);
                return $@"time {time:hh\:mm\:ss}";

            case SimulationClockCommand.Reset:
                clock.Reset();
                return "reset";

            case SimulationClockCommand.ResetSystem:
                clock.Reset(); // the modules are told separately (BroadcastSystemResetAsync)
                return "system reset";

            default:
                throw new ArgumentException($"Unknown clock command '{request.Command}'.");
        }
    }

    /// <summary>Sends the current state to every module that has no send still in flight.</summary>
    private void Publish()
    {
        StatePublished?.Invoke(this, Current);

        foreach (var subscriber in _subscribers)
        {
            // A previous send to this module is still stalled: skip it this time instead of
            // queueing behind it. The state is read when the send starts, so it is never stale.
            if (Interlocked.Exchange(ref subscriber.IsSending, 1) == 1)
            {
                continue;
            }

            _ = SendAsync(subscriber);
        }
    }

    private async Task SendAsync(Subscriber subscriber)
    {
        var state = Current;
        var message = new SystemTimeMessage
        {
            SystemTime = state.SystemTime,
            SpeedMultiplier = state.SpeedMultiplier,
            IsPaused = state.IsPaused,
            Sequence = state.Sequence,
        };

        try
        {
            await NamedPipeTransport.SendAsync(subscriber.PipeName, message, _shutdownToken, SubscriberConnectTimeoutMilliseconds)
                .ConfigureAwait(false);
            SetReachable(subscriber, true, "receiving");
        }
        catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
        {
        }
        catch (TimeoutException)
        {
            SetReachable(subscriber, false, "not running");
        }
        catch (Exception ex)
        {
            SetReachable(subscriber, false, ex.Message);
        }
        finally
        {
            Volatile.Write(ref subscriber.IsSending, 0);
        }
    }

    /// <summary>Every module that resets its own state on a ResetMessage.</summary>
    private static readonly (string Name, string Pipe)[] ResetTargets =
    {
        ("CTC", NamedPipeNames.Ctc),
        ("Track Controller", NamedPipeNames.TrackController),
        ("Track Model", NamedPipeNames.TrackModel),
        ("Train Model", NamedPipeNames.TrainModel),
        ("Train Controller", NamedPipeNames.TrainController),
    };

    /// <summary>
    /// Whole-system reset: sends one ResetMessage to every module (each clears its own state).
    /// A module that is not running is reported and skipped; the clock reset happens regardless.
    /// </summary>
    private async Task BroadcastSystemResetAsync()
    {
        var reset = new ResetMessage { ResetId = DateTime.UtcNow.Ticks };
        var failed = new List<string>();
        await Task.WhenAll(ResetTargets.Select(async target =>
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await NamedPipeTransport.SendAsync(target.Pipe, reset, _shutdownToken, 1000).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    await Task.Delay(200).ConfigureAwait(false);
                }
            }

            lock (failed)
            {
                failed.Add(target.Name);
            }
        })).ConfigureAwait(false);

        Report(failed.Count == 0
            ? "System reset sent to all modules."
            : $"System reset sent; not reached: {string.Join(", ", failed)} (not running?).");
    }

    private void SetReachable(Subscriber subscriber, bool reachable, string detail)
    {
        if (subscriber.IsReachable == reachable)
        {
            return;
        }

        subscriber.IsReachable = reachable;
        Debug.WriteLine($"[Simulation clock] {subscriber.Name}: {detail}");
        DeliveryStatusChanged?.Invoke(this, new ClockDeliveryStatus(subscriber.Name, reachable, detail));
    }

    private void Report(string status)
    {
        Debug.WriteLine($"[Simulation clock] {status}");
        StatusReported?.Invoke(this, status);
    }

    private sealed class Subscriber(string name, string pipeName)
    {
        public string Name { get; } = name;

        public string PipeName { get; } = pipeName;

        public int IsSending;

        // Null until the first send completes, so the first outcome is always reported.
        public bool? IsReachable { get; set; }
    }
}

/// <summary>Whether the clock's updates currently reach one module.</summary>
public sealed record ClockDeliveryStatus(string ModuleName, bool IsReachable, string Detail);
