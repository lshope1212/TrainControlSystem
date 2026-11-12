using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
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

    /// <summary>Cadence of the periodic outputs: block statuses to CTC, commands to the Track Model, diagnostics.</summary>
    private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(500);

    private readonly IWaysideRuntime _runtime;

    public WaysideSnapshotMessage LatestSnapshot => _runtime.LatestSnapshot;

    /// <summary>Reads current runtime state before allowing deployment or placement changes.</summary>
    public Task<WaysideSnapshotMessage> PollForSetupAsync(CancellationToken cancellationToken) => _runtime.PollAsync(cancellationToken);

    /// <summary>Returns the applied controller and execution target for a block.</summary>
    public string GetOwnerDescription(string blockId) => _runtime is TerritoryRuntimeRouter router ? router.GetOwnerDescription(blockId) : "Standalone controller · local PC";

    public string GetPlcStatus(string? territoryId) => _runtime is TerritoryRuntimeRouter router
        ? territoryId is null ? "" : router.GetTerritoryPlcStatus(territoryId)
        : LatestSnapshot.PlcStatus;

    private readonly string[] _pipes;
    private readonly CancellationTokenSource _stop = new();
    private Task[]? _workers;
    private readonly Dictionary<string, SwitchPosition> _trackModelSwitches = [];
    private readonly ConcurrentQueue<object> _monitorResults = new();
    // CTC's ordered outbox: request acknowledgements and dispatch statuses, sent ahead of status batches.
    private readonly ConcurrentQueue<object> _ctcAcknowledgements = new();
    private readonly SemaphoreSlim _ctcAcknowledgementQueued = new(0);

    // Physical dispatch coordination (CTC <-> Track Model); never part of PLC evaluation.
    private readonly TrainDispatchRelay _dispatch;

    // Wakes the Track Model publisher as soon as an input changes what is commanded for movement,
    // so a revocation (or a handoff) never waits for the next periodic batch. It stays one
    // publisher, so batches remain in order.
    private readonly SemaphoreSlim _trackModelOutputChanged = new(0);
    private string _lastMovementSignature = string.Empty;

    // Orders Track Model batches across this host's lifetime and its restarts (seeded from the clock).
    private long _trackModelSequence = DateTime.UtcNow.Ticks;
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
        _dispatch = new TrainDispatchRelay(() => _runtime.LatestSnapshot, _pipes[(int)Pipe.TrackModel], EnqueueForCtc, text => InputReceived?.Invoke(text));
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
            PublishCtcAsync(_stop.Token),
            PublishAsync(Pipe.TrackModel, _stop.Token),
            PublishAsync(Pipe.Monitor, _stop.Token),
            _dispatch.RunAsync(_stop.Token)
        ];
    }

    private async Task HandleAsync(MessageEnvelope envelope)
    {
        // Dispatch coordination is relayed at this integration layer; it never reaches the runtime or PLC.
        if (TrainDispatchRelay.Handles(envelope.MessageType))
        {
            _dispatch.Receive(envelope);
            return;
        }

        object result = await _runtime.ReceiveAsync(envelope, _stop.Token).ConfigureAwait(false);
        if (result is WaysideAcknowledgementMessage receipt)
        {
            AcknowledgementReceived?.Invoke(receipt);
        }

        SignalIfMovementOutputChanged(_runtime.LatestSnapshot);

        if (result is WaysideAcknowledgementMessage { IsAccepted: false } rejected)
        {
            InputReceived?.Invoke($"Rejected: {rejected.Detail}");
        }

        // CTC learns whether each of its requests was accepted for evaluation or rejected (and why);
        // delivery of its bytes alone tells it nothing. Sent by the CTC publisher, never from this
        // receive loop, so a busy CTC can never stall the controller's input pipe.
        if (CreateCtcAcknowledgement(envelope, result) is { } acknowledgement)
        {
            EnqueueForCtc(acknowledgement);
        }

        // Receipts and requested snapshots are diagnostics, not Track Model commands.
        _monitorResults.Enqueue(result);
        // Diagnostics are optional; retain recent receipts without growing indefinitely while the test UI is closed.
        while (_monitorResults.Count > 100) _monitorResults.TryDequeue(out _);
    }

    /// <summary>Queues an answer for CTC on its ordered publisher (bounded: CTC may be offline).</summary>
    private void EnqueueForCtc(object message)
    {
        _ctcAcknowledgements.Enqueue(message);
        while (_ctcAcknowledgements.Count > 100) _ctcAcknowledgements.TryDequeue(out _);
        _ctcAcknowledgementQueued.Release();
    }

    private async Task PublishAsync(Pipe pipe, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var snapshot = await _runtime.PollAsync(stop).ConfigureAwait(false);
                IEnumerable<object> messages = pipe == Pipe.TrackModel ? CreateTrackModelBatch(snapshot)
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
                    if (message is TrackModelCommandBatchMessage batch)
                    {
                        foreach (var command in batch.Commands)
                        {
                            _trackModelSwitches[command.BlockId] = command.Switch;
                        }
                    }
                }

                if (attempted && pipe != Pipe.Monitor)
                {
                    UpdateConnection(pipe == Pipe.Ctc, delivered);
                }

                if (pipe == Pipe.TrackModel)
                {
                    // Periodic, or at once when an input changed a movement output (e.g. a revocation).
                    await _trackModelOutputChanged.WaitAsync(PublishInterval, stop).ConfigureAwait(false);
                    while (_trackModelOutputChanged.CurrentCount > 0 && _trackModelOutputChanged.Wait(0))
                    {
                    }
                }
                else
                {
                    await Task.Delay(PublishInterval, stop).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return;
        }
    }

    /// <summary>
    /// The single CTC sender, so CTC receives everything in order: queued acknowledgements as soon
    /// as they exist, and every <see cref="PublishInterval"/> one <see cref="BlockStatusBatchMessage"/>
    /// with every owned block's status (one connection, not one per block: the three course lines
    /// are 241 blocks). Acknowledgements go first and never wait behind a status batch.
    /// </summary>
    private async Task PublishCtcAsync(CancellationToken stop)
    {
        var sinceStatuses = Stopwatch.StartNew();
        bool statusesDue = true;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                IEnumerable<object> statuses = statusesDue ? CreateCtcBatch(await _runtime.PollAsync(stop).ConfigureAwait(false)) : [];
                bool delivered = true;
                bool attempted = false;
                foreach (var acknowledgement in _ctcAcknowledgements.ToArray())
                {
                    attempted = true;
                    if (!await TrySendAsync(_pipes[(int)Pipe.Ctc], acknowledgement, stop).ConfigureAwait(false))
                    {
                        delivered = false;
                        break;
                    }

                    if (_ctcAcknowledgements.TryPeek(out var head) && ReferenceEquals(head, acknowledgement))
                    {
                        _ctcAcknowledgements.TryDequeue(out _);
                    }
                }

                foreach (object status in delivered ? statuses : [])
                {
                    attempted = true;
                    if (!await TrySendAsync(_pipes[(int)Pipe.Ctc], status, stop).ConfigureAwait(false))
                    {
                        delivered = false;
                        break;
                    }
                }

                if (statusesDue)
                {
                    sinceStatuses.Restart();
                }

                if (attempted && (statusesDue || !delivered))
                {
                    UpdateConnection(true, delivered);
                }

                // Wake early only for a new acknowledgement; statuses keep their cadence.
                var remaining = PublishInterval - sinceStatuses.Elapsed;
                statusesDue = remaining <= TimeSpan.Zero || !await _ctcAcknowledgementQueued.WaitAsync(remaining, stop).ConfigureAwait(false);
                while (_ctcAcknowledgementQueued.CurrentCount > 0 && _ctcAcknowledgementQueued.Wait(0))
                {
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Correlated answer to one of CTC's request types (movement, maintenance, switch). Track Model
    /// feedback and operator/test inputs get none.
    /// </summary>
    private static RequestAcknowledgementMessage? CreateCtcAcknowledgement(MessageEnvelope envelope, object result)
    {
        (string RequestId, string BlockId, string TrainId)? identity = envelope.MessageType switch
        {
            nameof(MovementSuggestionMessage) => ReadIdentity<MovementSuggestionMessage>(envelope, request => (request.RequestId, request.TargetBlockId, request.TrainId)),
            nameof(MaintenanceRequestMessage) => ReadIdentity<MaintenanceRequestMessage>(envelope, request => (request.RequestId, request.BlockId, "")),
            nameof(SwitchPositionRequestMessage) => ReadIdentity<SwitchPositionRequestMessage>(envelope, request => (request.RequestId, request.BlockId, "")),
            _ => null
        };
        if (identity is not { } request)
        {
            return null;
        }

        var receipt = result as WaysideAcknowledgementMessage;
        return new RequestAcknowledgementMessage
        {
            RequestId = request.RequestId ?? "",
            RequestType = envelope.MessageType,
            Outcome = receipt switch
            {
                { IsAccepted: true, IsStagedSuccessor: true } => RequestAcknowledgementOutcome.PreparedAsSuccessor,
                { IsAccepted: true } => RequestAcknowledgementOutcome.AcceptedForEvaluation,
                _ => RequestAcknowledgementOutcome.Rejected,
            },
            Detail = receipt?.Detail ?? "The controller runtime returned no acknowledgement.",
            BlockId = request.BlockId ?? "",
            TrainId = request.TrainId ?? "",
            AcknowledgedAtUtc = DateTimeOffset.UtcNow
        };
    }

    /// <summary>An unreadable request is still answered (the runtime rejects it); it just has no identity.</summary>
    private static (string, string, string) ReadIdentity<TRequest>(MessageEnvelope envelope, Func<TRequest, (string, string, string)> select)
        where TRequest : class
    {
        try
        {
            return select(MessageSerializer.DeserializePayload<TRequest>(envelope));
        }
        catch (JsonException)
        {
            return ("", "", "");
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

    /// <summary>The periodic CTC report: all block statuses of one evaluation in one message (none when nothing is owned).</summary>
    private static IEnumerable<object> CreateCtcBatch(WaysideSnapshotMessage snapshot)
    {
        var statuses = CreateCtcMessages(snapshot).ToList();
        return statuses.Count == 0 ? [] : [new BlockStatusBatchMessage { Statuses = statuses }];
    }

    /// <summary>
    /// The periodic Track Model output: every block's command of one evaluation in one message, in
    /// order (<see cref="TrackModelCommandBatchMessage.Sequence"/>) and with the evaluation's own
    /// validity as the authority lease, so the codes lapse to zero if this controller goes silent.
    /// </summary>
    private IEnumerable<object> CreateTrackModelBatch(WaysideSnapshotMessage snapshot)
    {
        var commands = CreateTrackModelMessages(snapshot).ToList();
        return commands.Count == 0 ? [] : [new TrackModelCommandBatchMessage
        {
            Commands = commands,
            Sequence = Interlocked.Increment(ref _trackModelSequence),
            AuthorityLeaseSeconds = Math.Clamp((snapshot.ValidUntilUtc - DateTimeOffset.UtcNow).TotalSeconds, 0, 5),
        }];
    }

    /// <summary>
    /// Wakes the Track Model publisher when what is commanded for movement changed: grants, their
    /// request IDs, limits and decisions, prepared handoffs, and signal aspects.
    /// </summary>
    private void SignalIfMovementOutputChanged(WaysideSnapshotMessage snapshot)
    {
        string signature = string.Join(";", snapshot.Blocks
            .Where(block => block.Request is not null || block.Continuation is not null)
            .Select(block => $"{block.BlockId}|{block.Request?.RequestId}|{block.MovementStatus}|{block.AuthorizedSpeedMetersPerSecond:R}|{block.AuthorizedAuthorityMeters:R}|{block.Continuation}"))
            + $"|{snapshot.IsPaused}|{snapshot.IsMaintenanceMode}"
            // Signal commands too: a prepared route's proceed aspect (whose report the grant waits for)
            // and a restrictive aspect after a revocation reach the Track Model without the periodic wait.
            + "|" + string.Join(",", snapshot.Blocks.Where(block => block.HasSignal).Select(block => $"{block.BlockId}:{block.CommandedSignal}"))
            // Switch commands too: a route's automatic alignment (whose reported position the grant waits for)
            // reaches the Track Model at once.
            + "|" + string.Join(",", snapshot.Blocks.Where(block => block.HasSwitch).Select(block => $"{block.BlockId}:{block.CommandedSwitch}"));
        lock (_connectionGate)
        {
            if (signature == _lastMovementSignature)
            {
                return;
            }

            _lastMovementSignature = signature;
        }

        _trackModelOutputChanged.Release();
    }

    private static IEnumerable<BlockStatusMessage> CreateCtcMessages(WaysideSnapshotMessage snapshot)
    {
        // Match the team's current CTC receiver contracts; reported equipment is never replaced by commands.
        bool current = snapshot.ValidUntilUtc > DateTimeOffset.UtcNow;
        foreach (var blockStatus in snapshot.Blocks)
        {
            yield return new BlockStatusMessage
            {
                BlockId = blockStatus.BlockId,
                Occupancy = blockStatus.IsFresh && !blockStatus.IsFaulted ? blockStatus.Occupancy : OccupancyState.Unknown,
                Signal = blockStatus.IsFresh ? blockStatus.ReportedSignal : SignalState.Unknown,
                Switch = blockStatus.IsFresh ? blockStatus.ReportedSwitch : SwitchPosition.Unknown,
                Crossing = blockStatus.IsFresh ? blockStatus.ReportedCrossing : CrossingState.Unknown,
                Maintenance = blockStatus.Maintenance == "Closed" ? MaintenanceState.Closed : MaintenanceState.Open,
                // Lets CTC drop a late report and detect that reports stopped (wall clock, like the watchdogs).
                ReportedAtUtc = snapshot.EvaluatedAtUtc,
                Movement = blockStatus.Request is { } request ? CreateMovementReport(snapshot, blockStatus, request, current) : null
            };
        }
    }

    /// <summary>What the controller actually commands for this request: the Track Model output, not just the evaluation.</summary>
    private static MovementAuthorizationReport CreateMovementReport(WaysideSnapshotMessage snapshot, WaysideBlockStatus block, MovementSuggestionMessage request, bool current)
    {
        var evaluated = block.MovementStatus switch
        {
            "Approved" => MovementAuthorizationState.Approved,
            "Restricted" => MovementAuthorizationState.Restricted,
            _ => MovementAuthorizationState.Withheld
        };
        bool granted = evaluated != MovementAuthorizationState.Withheld && IsMovementCommandPermitted(snapshot, block, current);
        return new MovementAuthorizationReport
        {
            RequestId = request.RequestId,
            TrainId = request.TrainId,
            State = granted ? evaluated : MovementAuthorizationState.Withheld,
            AuthorizedSpeedMetersPerSecond = granted ? block.AuthorizedSpeedMetersPerSecond : 0,
            AuthorizedAuthorityMeters = granted ? block.AuthorizedAuthorityMeters : 0,
            Reason = granted || evaluated == MovementAuthorizationState.Withheld ? block.MovementReason : WithheldReason(snapshot, block, current),
            SupersededRequestId = request.PredecessorRequestId ?? string.Empty
        };
    }

    private static string WithheldReason(WaysideSnapshotMessage snapshot, WaysideBlockStatus block, bool current) =>
        !current ? "Track Controller output expired."
        : snapshot.IsPaused ? "Track Controller is paused."
        : snapshot.IsMaintenanceMode ? "Track Controller maintenance mode is active."
        : !block.IsFresh || block.IsFaulted ? $"Block {block.BlockId} feedback is unavailable or faulted."
        : $"Movement from block {block.BlockId} is not permitted by the wayside.";

    /// <summary>The one rule for whether a movement result may be commanded to the track.</summary>
    private static bool IsMovementCommandPermitted(WaysideSnapshotMessage snapshot, WaysideBlockStatus block, bool current)
    {
        var request = block.Request;
        return current && block.IsFresh && !block.IsFaulted && !snapshot.IsPaused && !snapshot.IsMaintenanceMode
            && block.Occupancy == OccupancyState.Occupied && block.Maintenance != "Closed"
            && (!block.HasSwitch || block.CommandedSwitch is SwitchPosition.Normal or SwitchPosition.Reverse)
            && request is { AuthorityReference: MovementSuggestionMessage.BlockEntryReference } && request.TargetBlockId == block.BlockId
            && double.IsFinite(block.AuthorizedSpeedMetersPerSecond) && block.AuthorizedSpeedMetersPerSecond > 0
            && double.IsFinite(block.AuthorizedAuthorityMeters) && block.AuthorizedAuthorityMeters > 0;
    }

    private IEnumerable<TrackModelCommandMessage> CreateTrackModelMessages(WaysideSnapshotMessage snapshot)
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

            bool permitted = IsMovementCommandPermitted(snapshot, block, current);
            // A prepared handoff: the granted predecessor's own authority, from this next block's entry,
            // for a train entering from the predecessor's block. Coded only while that grant is commanded.
            var continuation = permitted ? null : PermittedContinuation(snapshot, block, current);
            yield return new TrackModelCommandMessage
            {
                BlockId = block.BlockId,
                CommandedSpeedMetersPerSecond = permitted ? block.AuthorizedSpeedMetersPerSecond : continuation?.AuthorizedSpeedMetersPerSecond ?? 0,
                // Preserve the fixed limit measured from this target block's entry; never copy it unchanged onto
                // route blocks. A continuation is the same end point measured from the next block's entry.
                AuthorityMeters = permitted ? block.AuthorizedAuthorityMeters : continuation?.AuthorizedAuthorityMeters ?? 0,
                MovementRequestId = permitted ? block.Request!.RequestId : continuation?.PredecessorRequestId ?? string.Empty,
                AuthorityEntryBlockId = continuation?.EntryBlockId ?? string.Empty,
                Switch = position,
                Signal = fresh && block.CommandedSignal is SignalState.Red or SignalState.Yellow or SignalState.Green or SignalState.SuperGreen
                    ? block.CommandedSignal : SignalState.Red,
                Crossing = fresh && block.CommandedCrossing is CrossingState.Open or CrossingState.Closed
                    ? block.CommandedCrossing : CrossingState.Closed
            };
        }
    }

    /// <summary>
    /// The block's continuation code, when the predecessor it belongs to is itself commanded to the
    /// track this cycle and the block is in a state a train may be authorized into.
    /// </summary>
    private static WaysideContinuation? PermittedContinuation(WaysideSnapshotMessage snapshot, WaysideBlockStatus block, bool current)
    {
        if (block.Continuation is not { } continuation || !current || !block.IsFresh || block.IsFaulted || block.Maintenance == "Closed"
            || block.Occupancy is not (OccupancyState.Clear or OccupancyState.Occupied)
            || !(continuation.AuthorizedAuthorityMeters > 0) || !(continuation.AuthorizedSpeedMetersPerSecond > 0))
        {
            return null;
        }

        var origin = snapshot.Blocks.FirstOrDefault(candidate => candidate.BlockId == continuation.EntryBlockId);
        return origin?.Request?.RequestId == continuation.PredecessorRequestId && IsMovementCommandPermitted(snapshot, origin, current)
            && continuation.AuthorizedAuthorityMeters < origin.AuthorizedAuthorityMeters
            && continuation.AuthorizedSpeedMetersPerSecond <= origin.AuthorizedSpeedMetersPerSecond
            ? continuation
            : null;
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
        _ctcAcknowledgementQueued.Dispose();
        _trackModelOutputChanged.Dispose();
    }
}
