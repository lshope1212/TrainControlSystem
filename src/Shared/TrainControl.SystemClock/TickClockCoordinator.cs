using TrainControl.Common.Communication;
using TrainControl.Contracts.Messages;

namespace TrainControl.SystemClock;

/// <summary>Configuration of the tick clock host.</summary>
/// <param name="Participants">Inbound pipe names of the EXTERNAL participants (other processes).</param>
/// <param name="TickTimeout">Wall-clock time to wait for every TickDone / ResetDone before the clock stops with a fault.</param>
public sealed record TickClockOptions(IReadOnlyList<string> Participants, double DeltaTimeSeconds = 0.1, TimeSpan? TickTimeout = null)
{
    public TimeSpan Timeout => TickTimeout ?? TimeSpan.FromSeconds(2);
}

/// <summary>
/// Global tick clock HOST (system timing design). A standalone component: whichever module is
/// configured as the host runs it (the Train Controller for now, CTC later); every other module
/// is only a participant. It contains no module logic.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Broadcasts <see cref="TickMessage"/> N to every participant, then waits for
///       <see cref="TickDoneMessage"/> N from all of them (barrier) before tick N + 1.</item>
/// <item>Paces ticks at dt / speed multiplier of wall-clock time; a slow participant slows the
///       simulation down, ticks are never skipped.</item>
/// <item>Run / Pause / Step / SetSpeed / Reset requests (local or <see cref="ClockControlMessage"/>)
///       take effect at a tick boundary; every change is broadcast as <see cref="ClockStateMessage"/>.</item>
/// <item>Reset: broadcast <see cref="ResetMessage"/>, wait for every <see cref="ResetDoneMessage"/>,
///       then restart from tick 1 (paused).</item>
/// <item>A participant that does not answer within the timeout stops the clock with a fault;
///       no tick is skipped and nothing is invented for it.</item>
/// </list>
/// </remarks>
public sealed class TickClockCoordinator : IAsyncDisposable
{
    private readonly TickClockOptions _options;
    private readonly Func<string, object, CancellationToken, Task> _send;
    private readonly object _gate = new object();
    private readonly SemaphoreSlim _wake = new SemaphoreSlim(0);
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private Task? _loop;

    private long _tickId;
    private bool _running;
    private int _pendingSteps;
    private bool _resetRequested;
    private double _speed = 1.0;
    private string _fault = string.Empty;
    private long _resetId;
    private long _awaitedTick = -1;
    private long _awaitedReset = -1;
    private HashSet<string> _outstanding = new HashSet<string>(StringComparer.Ordinal);
    private TaskCompletionSource? _barrier;

    /// <param name="send">Sends a message to a participant pipe (default: the shared named-pipe transport).</param>
    public TickClockCoordinator(TickClockOptions options, Func<string, object, CancellationToken, Task>? send = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _send = send ?? ((pipe, message, ct) => NamedPipeTransport.SendAsync(pipe, message, ct, 500));
    }

    /// <summary>Called (on the clock's thread) at the start of every tick, before participants are told.</summary>
    public event Func<TickMessage, CancellationToken, Task>? LocalTick;

    /// <summary>Called on reset, before the barrier completes.</summary>
    public event Func<CancellationToken, Task>? LocalReset;

    public event EventHandler<ClockStateMessage>? StateChanged;

    public event EventHandler<string>? Diagnostic;

    public ClockStateMessage State
    {
        get
        {
            lock (_gate)
            {
                return new ClockStateMessage
                {
                    TickId = _tickId,
                    SimulationTimeSeconds = _tickId * _options.DeltaTimeSeconds,
                    IsRunning = _running,
                    SpeedMultiplier = _speed,
                    Fault = _fault,
                };
            }
        }
    }

    public void Start()
    {
        _loop ??= Task.Run(() => LoopAsync(_cts.Token));
    }

    // ---------------------------------------------------------------- control (thread-safe)

    public void Run() => Change(() => { _running = true; _fault = string.Empty; });

    public void Pause() => Change(() => _running = false);

    public void Step() => Change(() => { _running = false; _pendingSteps++; _fault = string.Empty; });

    public void Reset() => Change(() => { _running = false; _resetRequested = true; });

    public void SetSpeed(double multiplier)
    {
        if (!(multiplier >= 1.0 && multiplier <= 10.0))
        {
            throw new ArgumentOutOfRangeException(nameof(multiplier), multiplier, "Speed must be between 1x and 10x.");
        }

        Change(() => _speed = multiplier);
    }

    /// <summary>Handles a clock message received by the host module's pipe receiver.</summary>
    public void Handle(object message)
    {
        switch (message)
        {
            case TickDoneMessage done:
                Arrived(done.Participant, isTick: true, done.TickId);
                break;
            case ResetDoneMessage done:
                Arrived(done.Participant, isTick: false, done.ResetId);
                break;
            case ClockControlMessage control:
                Diagnostic?.Invoke(this, $"Clock request {control.Command} from {control.Requester}.");
                switch (control.Command)
                {
                    case ClockCommand.Run: Run(); break;
                    case ClockCommand.Pause: Pause(); break;
                    case ClockCommand.Step: Step(); break;
                    case ClockCommand.SetSpeed: SetSpeed(Math.Clamp(control.SpeedMultiplier, 1.0, 10.0)); break;
                    case ClockCommand.Reset: Reset(); break;
                }

                break;
        }
    }

    // ---------------------------------------------------------------- loop

    private void Change(Action change)
    {
        lock (_gate)
        {
            change();
        }

        _wake.Release();
        _ = BroadcastStateAsync(_cts.Token);
    }

    private async Task LoopAsync(CancellationToken token)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (!token.IsCancellationRequested)
            {
                bool reset, tick;
                double speed;
                lock (_gate)
                {
                    reset = _resetRequested;
                    tick = !reset && (_running || _pendingSteps > 0);
                    if (tick && !_running) _pendingSteps--;
                    speed = _speed;
                }

                if (reset)
                {
                    await DoResetAsync(token).ConfigureAwait(false);
                    continue;
                }

                if (!tick)
                {
                    // Idle: wake on a request, or re-check periodically.
                    await _wake.WaitAsync(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
                    continue;
                }

                watch.Restart();
                await DoTickAsync(token).ConfigureAwait(false);

                var target = TimeSpan.FromSeconds(_options.DeltaTimeSeconds / speed);
                var remaining = target - watch.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DoTickAsync(CancellationToken token)
    {
        TickMessage tick;
        lock (_gate)
        {
            _tickId++;
            tick = new TickMessage { TickId = _tickId, SimulationTimeSeconds = _tickId * _options.DeltaTimeSeconds, DeltaTimeSeconds = _options.DeltaTimeSeconds };
            Arm(isTick: true, _tickId);
        }

        if (LocalTick is { } local)
        {
            await local(tick, token).ConfigureAwait(false);
        }

        await BroadcastAsync(tick, token).ConfigureAwait(false);
        if (!await WaitBarrierAsync(token).ConfigureAwait(false))
        {
            Fail($"Tick {tick.TickId}: no TickDone from {Missing()} within {_options.Timeout.TotalSeconds:0.#} s.");
        }
    }

    private async Task DoResetAsync(CancellationToken token)
    {
        long id;
        lock (_gate)
        {
            _resetRequested = false;
            _pendingSteps = 0;
            id = ++_resetId;
            Arm(isTick: false, id);
        }

        if (LocalReset is { } local)
        {
            await local(token).ConfigureAwait(false);
        }

        await BroadcastAsync(new ResetMessage { ResetId = id }, token).ConfigureAwait(false);
        var ok = await WaitBarrierAsync(token).ConfigureAwait(false);
        lock (_gate)
        {
            _tickId = 0;
            _running = false;
            _fault = ok ? string.Empty : $"Reset {id}: no ResetDone from {Missing()}.";
        }

        Diagnostic?.Invoke(this, ok ? "Reset complete; restarting from tick 1 when run." : _fault);
        await BroadcastStateAsync(token).ConfigureAwait(false);
    }

    private void Arm(bool isTick, long id)
    {
        _outstanding = new HashSet<string>(_options.Participants, StringComparer.Ordinal);
        _awaitedTick = isTick ? id : -1;
        _awaitedReset = isTick ? -1 : id;
        _barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_outstanding.Count == 0)
        {
            _barrier.TrySetResult();
        }
    }

    private void Arrived(string participant, bool isTick, long id)
    {
        lock (_gate)
        {
            var expected = isTick ? _awaitedTick : _awaitedReset;
            if (id != expected || !_outstanding.Remove(participant))
            {
                return; // stale, duplicate or unknown: ignored
            }

            if (_outstanding.Count == 0)
            {
                _barrier?.TrySetResult();
            }
        }
    }

    private async Task<bool> WaitBarrierAsync(CancellationToken token)
    {
        Task barrier;
        lock (_gate)
        {
            barrier = _barrier?.Task ?? Task.CompletedTask;
        }

        var finished = await Task.WhenAny(barrier, Task.Delay(_options.Timeout, token)).ConfigureAwait(false);
        return finished == barrier;
    }

    private string Missing()
    {
        lock (_gate)
        {
            return string.Join(", ", _outstanding);
        }
    }

    private void Fail(string reason)
    {
        lock (_gate)
        {
            _running = false;
            _pendingSteps = 0;
            _fault = reason;
        }

        Diagnostic?.Invoke(this, reason);
        _ = BroadcastStateAsync(_cts.Token);
    }

    private async Task BroadcastStateAsync(CancellationToken token)
    {
        var state = State;
        StateChanged?.Invoke(this, state);
        await BroadcastAsync(state, token).ConfigureAwait(false);
    }

    private async Task BroadcastAsync(object message, CancellationToken token)
    {
        foreach (var pipe in _options.Participants)
        {
            try
            {
                await _send(pipe, message, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Diagnostic?.Invoke(this, $"Could not reach {pipe}: {ex.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        _cts.Dispose();
    }
}
