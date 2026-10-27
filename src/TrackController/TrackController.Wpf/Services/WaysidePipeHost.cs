using System.Collections.Concurrent;
using System.IO;
using TrackController.Contracts.Messages;
using TrackController.Core.Services;
using TrackController.Runtime;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Wpf.Services;

/// <summary>One production receiver/publisher implementation; tests only substitute endpoint addresses.</summary>
public sealed class WaysidePipeHost : IAsyncDisposable
{
    public enum Pipe { Input, Ctc, TrackModel, Monitor }

    private readonly IWaysideRuntime _runtime;

    public WaysideSnapshotMessage LatestSnapshot => _runtime.LatestSnapshot;

    /// <summary>Reads current runtime state before allowing deployment or placement changes.</summary>
    public Task<WaysideSnapshotMessage> PollForSetupAsync(CancellationToken cancellationToken) => _runtime.PollAsync(cancellationToken);

    /// <summary>Returns the applied controller and execution target for a block.</summary>
    public string GetOwnerDescription(string blockId) => _runtime is TerritoryRuntimeRouter router ? router.GetOwnerDescription(blockId) : "Standalone controller · local PC";

    private readonly string[] _pipes;
    private readonly CancellationTokenSource _stop = new();
    private Task[]? _workers;
    private readonly Dictionary<string, SwitchPosition> _trackModelSwitches = [];
    private readonly ConcurrentQueue<object> _monitorResults = new();
    private readonly object _connectionGate = new();
    private bool? _ctcConnected;
    private bool? _trackModelConnected;

    public event Action<string>? StatusChanged;

    public event Action<string>? InputReceived;

    public event Action<WaysideAcknowledgementMessage>? AcknowledgementReceived;

    /// <summary>Connects a runtime to the production named-pipe receiver and output publishers.</summary>
    public WaysidePipeHost(TrackControllerService controller, string[]? pipeNames = null) : this(new LocalWaysideRuntime(controller), pipeNames)
    {
    }

    /// <summary>Connects a runtime to the production named-pipe receiver and output publishers.</summary>
    public WaysidePipeHost(IWaysideRuntime runtime, string[]? pipeNames = null)
    {
        _runtime = runtime;
        if (pipeNames is { Length: not 4 }) throw new ArgumentException("Provide one name for each pipe role.", nameof(pipeNames));
        _pipes = pipeNames is null ? CreatePipeNames() : [.. pipeNames];
    }

    /// <summary>Builds addresses in Pipe enum order; tests can request an isolated set.</summary>
    public static string[] CreatePipeNames(bool isolated = false)
    {
        string? prefix = isolated ? $"TrainControl.TrackController.Test.{Guid.NewGuid():N}" : null;
        string input = prefix is null ? NamedPipeNames.TrackController : prefix + ".Input";
        return [input, prefix is null ? NamedPipeNames.Ctc : prefix + ".CTC",
            prefix is null ? NamedPipeNames.TrackModel : prefix + ".TrackModel", input + ".Monitor"];
    }

    /// <summary>Starts the production input receiver and independent CTC and Track Model publishers.</summary>
    public void Start()
    {
        if (_workers is not null)
        {
            throw new InvalidOperationException("Pipe host already started.");
        }

        _workers =
        [
            NamedPipeTransport.ListenAsync(_pipes[(int)Pipe.Input], HandleAsync, ex => InputReceived?.Invoke($"Input connection: {ex.Message}"), _stop.Token),
            PublishAsync(Pipe.Ctc, _stop.Token),
            PublishAsync(Pipe.TrackModel, _stop.Token),
            PublishAsync(Pipe.Monitor, _stop.Token)
        ];
    }

    private async Task HandleAsync(MessageEnvelope envelope)
    {
        object result = await _runtime.ReceiveAsync(envelope, _stop.Token).ConfigureAwait(false);
        if (result is WaysideAcknowledgementMessage receipt)
        {
            AcknowledgementReceived?.Invoke(receipt);
        }

        if (result is WaysideAcknowledgementMessage { IsAccepted: false } rejected)
        {
            InputReceived?.Invoke($"Rejected: {rejected.Detail}");
        }
        // Receipts and requested snapshots are diagnostics, not Track Model commands.
        _monitorResults.Enqueue(result);
        // Diagnostics are optional; retain recent receipts without growing indefinitely while the test UI is closed.
        while (_monitorResults.Count > 100) _monitorResults.TryDequeue(out _);
    }

    private async Task PublishAsync(Pipe pipe, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var snapshot = await _runtime.PollAsync(stop).ConfigureAwait(false);
                IEnumerable<object> messages = pipe == Pipe.Ctc ? CreateCtcMessages(snapshot)
                    : pipe == Pipe.TrackModel ? CreateTrackModelMessages(snapshot)
                    : _monitorResults.ToArray().Append(snapshot);
                bool delivered = true;
                bool attempted = false;
                foreach (object message in messages)
                {
                    attempted = true;
                    if (!await TrySendAsync(_pipes[(int)pipe], message, stop).ConfigureAwait(false))
                    {
                        delivered = false;
                        break;
                    }
                    if (pipe == Pipe.Monitor && _monitorResults.TryPeek(out var pending) && ReferenceEquals(message, pending))
                    {
                        _monitorResults.TryDequeue(out _);
                    }
                    if (pipe == Pipe.TrackModel && message is TrackModelCommandMessage command)
                    {
                        _trackModelSwitches[command.BlockId] = command.Switch;
                    }
                }

                if (attempted && pipe != Pipe.Monitor)
                {
                    UpdateConnection(pipe == Pipe.Ctc, delivered);
                }
                await Task.Delay(500, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return;
        }
    }

    private void UpdateConnection(bool ctc, bool delivered)
    {
        lock (_connectionGate)
        {
            if ((ctc ? _ctcConnected : _trackModelConnected) == delivered)
            {
                return;
            }

            if (ctc)
            {
                _ctcConnected = delivered;
            }
            else
            {
                _trackModelConnected = delivered;
            }

            // Both independent publishers share one footer; report state changes, not each packet.
            StatusChanged?.Invoke($"CTC: {ConnectionState(_ctcConnected)} · Track Model: {ConnectionState(_trackModelConnected)}");
        }
    }

    private static string ConnectionState(bool? connected) => connected switch
    {
        true => "connected",
        false => "unavailable",
        _ => "checking"
    };

    private IEnumerable<object> CreateCtcMessages(WaysideSnapshotMessage snapshot)
    {
        // Match the team's current CTC receiver contracts; reported equipment is never replaced by commands.
        foreach (var blockStatus in snapshot.Blocks)
        {
            yield return new BlockStatusMessage
            {
                BlockId = blockStatus.BlockId,
                Occupancy = blockStatus.IsFresh && !blockStatus.IsFaulted ? blockStatus.Occupancy : OccupancyState.Unknown,
                Signal = blockStatus.IsFresh ? blockStatus.ReportedSignal : SignalState.Unknown,
                Switch = blockStatus.IsFresh ? blockStatus.ReportedSwitch : SwitchPosition.Unknown,
                Crossing = blockStatus.IsFresh ? blockStatus.ReportedCrossing : CrossingState.Unknown,
                Maintenance = blockStatus.Maintenance == "Closed" ? MaintenanceState.Closed : MaintenanceState.Open
            };
        }


    }

    private IEnumerable<object> CreateTrackModelMessages(WaysideSnapshotMessage snapshot)
    {
        bool current = snapshot.ValidUntilUtc > DateTimeOffset.UtcNow;
        foreach (var block in snapshot.Blocks)
        {
            bool fresh = current && block.IsFresh && !block.IsFaulted;
            var position = fresh ? block.CommandedSwitch : SwitchPosition.Unknown;
            if (!block.HasSwitch)
            {
                position = SwitchPosition.Normal; // Required by the wire contract; ignored for blocks without a switch.
            }
            else
            {
                if (!fresh && _trackModelSwitches.TryGetValue(block.BlockId, out var held)) position = held;
                if (position is not (SwitchPosition.Normal or SwitchPosition.Reverse)) position = block.ReportedSwitch;
                if (position is not (SwitchPosition.Normal or SwitchPosition.Reverse)) _trackModelSwitches.TryGetValue(block.BlockId, out position);
                // The receiver has no "hold switch" command. Never invent a position when none is known.
                if (position is not (SwitchPosition.Normal or SwitchPosition.Reverse)) continue;
            }

            var request = block.Request;
            bool permitted = fresh && !snapshot.IsPaused && !snapshot.IsMaintenanceMode
                && block.Occupancy == OccupancyState.Occupied && block.Maintenance != "Closed"
                && (!block.HasSwitch || block.CommandedSwitch is SwitchPosition.Normal or SwitchPosition.Reverse)
                && request is { AuthorityReference: MovementSuggestionMessage.BlockEntryReference } && request.TargetBlockId == block.BlockId
                && double.IsFinite(block.AuthorizedSpeedMetersPerSecond) && block.AuthorizedSpeedMetersPerSecond > 0
                && double.IsFinite(block.AuthorizedAuthorityMeters) && block.AuthorizedAuthorityMeters > 0;
            yield return new TrackModelCommandMessage
            {
                BlockId = block.BlockId,
                CommandedSpeedMetersPerSecond = permitted ? block.AuthorizedSpeedMetersPerSecond : 0,
                // Preserve the fixed limit measured from this target block's entry; do not copy it onto route blocks.
                AuthorityMeters = permitted ? block.AuthorizedAuthorityMeters : 0,
                Switch = position,
                Signal = fresh && block.CommandedSignal is SignalState.Red or SignalState.Yellow or SignalState.Green or SignalState.SuperGreen
                    ? block.CommandedSignal : SignalState.Red,
                Crossing = fresh && block.CommandedCrossing is CrossingState.Open or CrossingState.Closed
                    ? block.CommandedCrossing : CrossingState.Closed
            };
        }
    }

    private static async Task<bool> TrySendAsync(string pipe, object message, CancellationToken stop)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
            deadline.CancelAfter(300);
            await NamedPipeTransport.SendAsync(pipe, message, deadline.Token, connectTimeoutMilliseconds: 250).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Stops owned background operations and releases connections before returning.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_workers is not null)
        {
            await Task.WhenAll(_workers);
        }

        await _runtime.DisposeAsync();
        _stop.Dispose();
    }
}
