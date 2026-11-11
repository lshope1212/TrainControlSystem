using TrainControl.Common.Communication;
using TrainControl.Common.Identity;
using TrainController.Abstractions.Fleet;
using TrainControl.SystemClock;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;
using TrainController.Integration.Logging;

namespace TrainController.Integration.Communication;

/// <summary>
/// Normal mode inbound endpoint on <see cref="NamedPipeNames.TrainController"/>:
/// <list type="bullet">
/// <item><see cref="TrainModelStatusMessage"/> (tick N state of one train) → submitted and that
///       train is computed at once; its commands go back tagged with TickId N (same-tick pairing).</item>
/// <item>Clock messages: forwarded to the hosted clock (TickDone / ResetDone / ClockControl), or,
///       when another module hosts the clock, answered as a participant (Tick / Reset).</item>
/// </list>
/// Until the Train Model sends event flags, a new authority is inferred from the first value, a
/// changed value or the signal becoming valid again, and a new beacon from a changed station
/// name or distance.
/// </summary>
public sealed class TrainControllerPipeReceiver
{
    private readonly TrainControllerSubsystem _subsystem;
    private readonly string? _participantHostPipe;
    private readonly Func<string, object, CancellationToken, Task> _send;
    private readonly Dictionary<string, (bool Known, double Authority, bool SignalValid, string Station, double Distance)> _seen =
        new Dictionary<string, (bool, double, bool, string, double)>(StringComparer.Ordinal);
    /// <summary>
    /// A gap of more than this many steps is clamped. Gaps are normal when the controller skips
    /// stale states under load (latest-state mailbox), so the limit is generous: the Train Model
    /// itself restarts its count after a jump of more than 10 s.
    /// </summary>
    public const int MaxStepsPerState = 100;

    private readonly Dictionary<string, long> _lastTick = new Dictionary<string, long>(StringComparer.Ordinal);
    private readonly HashSet<string> _warned = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, TrainModelStatusMessage> _mailbox = new Dictionary<string, TrainModelStatusMessage>(StringComparer.Ordinal);
    private readonly HashSet<string> _busy = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, SignalState> _trafficLights = new Dictionary<string, SignalState>(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <param name="participantHostPipe">Set when ANOTHER module hosts the clock: its pipe, for TickDone / ResetDone.</param>
    public TrainControllerPipeReceiver(
        TrainControllerSubsystem subsystem,
        string? participantHostPipe = null,
        Func<string, object, CancellationToken, Task>? send = null)
    {
        _subsystem = subsystem ?? throw new ArgumentNullException(nameof(subsystem));
        _participantHostPipe = participantHostPipe;
        _send = send ?? ((pipe, message, ct) => NamedPipeTransport.SendAsync(pipe, message, ct, 500));
    }

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => NamedPipeTransport.ListenAsync(NamedPipeNames.TrainController, e => HandleAsync(e, token), OnError, token));
        _subsystem.Log.Log(TrainControllerLogLevel.Info, "TrainModelLink", null, $"Listening on pipe '{NamedPipeNames.TrainController}'.");
    }

    public async Task StopAsync()
    {
        if (_cts is null || _loop is null) return;
        _cts.Cancel();
        try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    /// <summary>Handles one received envelope (public for tests).</summary>
    public async Task HandleAsync(MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        switch (envelope.MessageType)
        {
            case nameof(TrainModelStatusMessage):
                // Do not compute on the pipe thread: hand the state to that train's worker and
                // accept the next message at once (see Post).
                Post(MessageSerializer.DeserializePayload<TrainModelStatusMessage>(envelope), cancellationToken);
                break;
            case nameof(TrackModelSignalMessage):
                HandleSignal(MessageSerializer.DeserializePayload<TrackModelSignalMessage>(envelope));
                break;
            case nameof(TickDoneMessage):
                _subsystem.SystemClock?.Handle(MessageSerializer.DeserializePayload<TickDoneMessage>(envelope));
                break;
            case nameof(ResetDoneMessage):
                _subsystem.SystemClock?.Handle(MessageSerializer.DeserializePayload<ResetDoneMessage>(envelope));
                break;
            case nameof(ClockControlMessage):
                _subsystem.SystemClock?.Handle(MessageSerializer.DeserializePayload<ClockControlMessage>(envelope));
                break;
            case nameof(TickMessage) when _participantHostPipe is not null:
                var tick = MessageSerializer.DeserializePayload<TickMessage>(envelope);
                await _send(_participantHostPipe, new TickDoneMessage { TickId = tick.TickId, Participant = NamedPipeNames.TrainController }, cancellationToken).ConfigureAwait(false);
                break;
            case nameof(ResetMessage):
                // From the clock host (participant mode) or from the Train Model (its reset).
                var reset = MessageSerializer.DeserializePayload<ResetMessage>(envelope);
                await ResetLocalAsync(cancellationToken).ConfigureAwait(false);
                _subsystem.Log.Log(TrainControllerLogLevel.Info, "TrainModelLink", null, "Reset received: all trains' controller state cleared.");
                if (_participantHostPipe is not null)
                {
                    await _send(_participantHostPipe, new ResetDoneMessage { ResetId = reset.ResetId, Participant = NamedPipeNames.TrainController }, cancellationToken).ConfigureAwait(false);
                }

                break;
            case nameof(ClockStateMessage):
                break; // display only; the hosted clock reports its own state
            default:
                _subsystem.Log.Log(TrainControllerLogLevel.Warning, "TrainModelLink", null, $"Ignored unexpected message type '{envelope.MessageType}'.");
                break;
        }
    }

    /// <summary>
    /// Latest-state mailbox per train. Each train has at most one computation in flight; if
    /// newer states arrive meanwhile, only the newest is kept (the TickId gap gives the elapsed
    /// time). So the controller never falls behind and answers stale states (e.g. at 10x, or
    /// while a hardware train waits for the Pi), and trains do not hold each other up.
    /// </summary>
    public void Post(TrainModelStatusMessage status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        var key = status.TrainId ?? string.Empty;
        lock (_mailbox)
        {
            _mailbox[key] = status;
            if (!_busy.Add(key))
            {
                return; // a worker is running for this train; it will pick up the newest state
            }
        }

        _ = Task.Run(() => DrainAsync(key, cancellationToken));
    }

    private async Task DrainAsync(string key, CancellationToken cancellationToken)
    {
        while (true)
        {
            TrainModelStatusMessage? next;
            lock (_mailbox)
            {
                if (!_mailbox.Remove(key, out next))
                {
                    _busy.Remove(key);
                    return;
                }
            }

            try
            {
                await HandleStatusAsync(next, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (_mailbox) { _busy.Remove(key); }
                return;
            }
            catch (Exception ex)
            {
                OnError(ex);
            }
        }
    }

    /// <summary>Waits until every train's pending state has been processed (tests).</summary>
    public async Task FlushAsync()
    {
        while (true)
        {
            lock (_mailbox)
            {
                if (_busy.Count == 0 && _mailbox.Count == 0) return;
            }

            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    /// <summary>Submits one train's tick-N state and computes that train's tick N.</summary>
    public async Task HandleStatusAsync(TrainModelStatusMessage status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);

        // Canonical train IDs ("007") cross the pipe; the fleet uses "TRAIN-007" internally.
        if (!TrainIdConvention.TryNormalize(status.TrainId, out var canonical)
            || !TrainFleet.IsKnownTrain(TrainIdConvention.ToTrainControllerLabel(canonical)))
        {
            WarnOnce(status.TrainId, $"Ignored state for train '{status.TrainId}': not a canonical ID of this controller's fleet ({TrainFleet.AllTrainIds[0]}…{TrainFleet.AllTrainIds[^1]}).");
            return;
        }

        status.TrainId = TrainIdConvention.ToTrainControllerLabel(canonical);

        // Elapsed simulation time since this train's previous state (TickId = the Train
        // Model's step count). A TickId that goes backwards means the Train Model was reset.
        double dt;
        lock (_lastTick)
        {
            if (_lastTick.TryGetValue(status.TrainId, out var previous) && status.TickId < previous)
            {
                _lastTick.Clear();
                dt = double.NaN;
            }
            else
            {
                var steps = _lastTick.TryGetValue(status.TrainId, out previous) ? status.TickId - previous : 1;
                if (steps <= 0)
                {
                    return; // duplicate or stale state: already answered
                }

                dt = Math.Min(steps, MaxStepsPerState) * _subsystem.Engine.DeltaTimeSeconds;
                _lastTick[status.TrainId] = status.TickId;
            }
        }

        if (double.IsNaN(dt))
        {
            _subsystem.Log.Log(TrainControllerLogLevel.Warning, "TrainModelLink", status.TrainId, "TickId went backwards: treating it as a Train Model reset.");
            await ResetLocalAsync(cancellationToken).ConfigureAwait(false);
            lock (_lastTick)
            {
                _lastTick[status.TrainId] = status.TickId;
            }

            dt = _subsystem.Engine.DeltaTimeSeconds;
        }

        // Traffic light ahead: sent by the Track Model directly to this controller (the Train
        // Model leaves it Unknown); the latest aspect for this train is applied to its state.
        if (status.TrafficLight == SignalState.Unknown)
        {
            lock (_trafficLights)
            {
                if (_trafficLights.TryGetValue(status.TrainId, out var aspect))
                {
                    status.TrafficLight = aspect;
                }
            }
        }

        bool authorityUpdated, beaconNew;
        lock (_seen)
        {
            _seen.TryGetValue(status.TrainId, out var last);
            authorityUpdated = status.TrackSignalValid
                && (!last.Known || !last.Authority.Equals(status.CommandedAuthorityMeters) || !last.SignalValid);
            var station = status.NextStationName ?? string.Empty;
            beaconNew = status.BeaconValid && station.Length > 0
                && (!string.Equals(station, last.Station, StringComparison.Ordinal) || !status.DistanceToNextStationMeters.Equals(last.Distance));
            _seen[status.TrainId] = (
                last.Known || status.TrackSignalValid,
                status.TrackSignalValid ? status.CommandedAuthorityMeters : last.Authority,
                status.TrackSignalValid,
                status.BeaconValid ? station : last.Station,
                status.BeaconValid ? status.DistanceToNextStationMeters : last.Distance);
        }

        _subsystem.NormalModelInput.Submit(status, beaconNew, authorityUpdated);
        await _subsystem.Engine.ExecuteTrainAsync(status.TrainId, status.TickId, dt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Global reset: clears every train's controller runtime and the event inference.</summary>
    public async Task ResetLocalAsync(CancellationToken cancellationToken)
    {
        lock (_seen)
        {
            _seen.Clear();
        }

        lock (_lastTick)
        {
            _lastTick.Clear();
        }

        lock (_trafficLights)
        {
            _trafficLights.Clear();
        }

        await _subsystem.Engine.ResetAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Track Model → Train Controller: the signal aspect ahead of one train.</summary>
    public void HandleSignal(TrackModelSignalMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!TrainIdConvention.TryNormalize(message.TrainId, out var canonical))
        {
            WarnOnce("signal:" + message.TrainId, $"Ignored signal for train '{message.TrainId}': not a canonical train ID.");
            return;
        }

        lock (_trafficLights)
        {
            _trafficLights[TrainIdConvention.ToTrainControllerLabel(canonical)] = message.Signal;
        }
    }

    private void WarnOnce(string key, string text)
    {
        lock (_warned)
        {
            if (!_warned.Add(key ?? string.Empty))
            {
                return;
            }
        }

        _subsystem.Log.Log(TrainControllerLogLevel.Warning, "TrainModelLink", null, text);
    }

    private void OnError(Exception ex) =>
        _subsystem.Log.Log(TrainControllerLogLevel.Warning, "TrainModelLink", null, $"Pipe: {ex.Message}");
}
