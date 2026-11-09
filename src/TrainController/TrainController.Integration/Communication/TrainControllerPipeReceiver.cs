using TrainControl.Common.Communication;
using TrainControl.SystemClock;
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
                await HandleStatusAsync(MessageSerializer.DeserializePayload<TrainModelStatusMessage>(envelope), cancellationToken).ConfigureAwait(false);
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
            case nameof(ResetMessage) when _participantHostPipe is not null:
                var reset = MessageSerializer.DeserializePayload<ResetMessage>(envelope);
                await ResetLocalAsync(cancellationToken).ConfigureAwait(false);
                await _send(_participantHostPipe, new ResetDoneMessage { ResetId = reset.ResetId, Participant = NamedPipeNames.TrainController }, cancellationToken).ConfigureAwait(false);
                break;
            case nameof(ClockStateMessage):
                break; // display only; the hosted clock reports its own state
            default:
                _subsystem.Log.Log(TrainControllerLogLevel.Warning, "TrainModelLink", null, $"Ignored unexpected message type '{envelope.MessageType}'.");
                break;
        }
    }

    /// <summary>Submits one train's tick-N state and computes that train's tick N.</summary>
    public async Task HandleStatusAsync(TrainModelStatusMessage status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        bool authorityUpdated, beaconNew;
        lock (_seen)
        {
            _seen.TryGetValue(status.TrainId, out var last);
            authorityUpdated = status.TrackSignalValid
                && (!last.Known || !last.Authority.Equals(status.RemainingAuthorityMeters) || !last.SignalValid);
            var station = status.NextStationName ?? string.Empty;
            beaconNew = status.BeaconValid && station.Length > 0
                && (!string.Equals(station, last.Station, StringComparison.Ordinal) || !status.DistanceToNextStationMeters.Equals(last.Distance));
            _seen[status.TrainId] = (
                last.Known || status.TrackSignalValid,
                status.TrackSignalValid ? status.RemainingAuthorityMeters : last.Authority,
                status.TrackSignalValid,
                status.BeaconValid ? station : last.Station,
                status.BeaconValid ? status.DistanceToNextStationMeters : last.Distance);
        }

        _subsystem.NormalModelInput.Submit(status, beaconNew, authorityUpdated);
        await _subsystem.Engine.ExecuteTrainAsync(status.TrainId, status.TickId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Global reset: clears every train's controller runtime and the event inference.</summary>
    public async Task ResetLocalAsync(CancellationToken cancellationToken)
    {
        lock (_seen)
        {
            _seen.Clear();
        }

        await _subsystem.Engine.ResetAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnError(Exception ex) =>
        _subsystem.Log.Log(TrainControllerLogLevel.Warning, "TrainModelLink", null, $"Pipe: {ex.Message}");
}
