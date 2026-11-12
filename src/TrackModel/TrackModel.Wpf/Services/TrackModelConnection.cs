using System.Diagnostics;
using System.Threading.Channels;
using System.Windows.Threading;
using TrackModel.Core.Interfaces;
using TrackModel.Core.Models;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackModel.Wpf.Services;

/// <summary>Named-pipe endpoints of the Track Model connection; tests substitute isolated names.</summary>
public sealed record TrackModelEndpoints(string Inbound, string TrackController, string TrainModel, string Ctc, string TestUi)
{
    // Existing five-argument callers keep their API; custom connections stay on isolated pipes.
    public string TrainController { get; init; } = Inbound == NamedPipeNames.TrackModel
        ? NamedPipeNames.TrainController : Inbound + ".TrainController";

    public static TrackModelEndpoints Production { get; } = new(
        NamedPipeNames.TrackModel, NamedPipeNames.TrackController, NamedPipeNames.TrainModel, NamedPipeNames.Ctc, NamedPipeNames.TrackModelTestUi);
}

/// <summary>Infrastructure only: serializes mutations on the UI thread, then sends
/// immutable contract snapshots to the receiving module endpoints.</summary>
/// <remarks>
/// <para>
/// Outputs: block states and dispatch statuses to the Track Controller, train environments and
/// activations to the Train Model, the layout and ticket sales to CTC, per-train light states to the
/// Train Controller, and private setup to the standalone TestUI. Lights have their own sender and update
/// immediately on state changes, on snapshot requests and on heartbeat. A state change publishes one full
/// snapshot (debounced). The layout goes to CTC only when CTC does not have the current revision yet: on
/// start-up, after a layout change, after an explicit <see cref="TrackModelSnapshotRequestMessage"/>, and
/// again on every heartbeat while CTC has not received it (e.g. CTC started after the Track Model).
/// </para>
/// <para>
/// Block states go to the Track Controller as one <c>TrackModelBlockStates</c> batch per line (the Track
/// Controller's existing batch input), not one connection per block: the three course lines are 241
/// blocks. A Track Controller that does not own a line rejects that line's batch only. Everything for the
/// Track Controller goes through ONE ordered sender: full snapshots (a newer unsent snapshot replaces an
/// older one), and every train occupancy step as its own observation of that line, never merged or
/// dropped, so the Track Controller sees each block boundary a train crosses (the prepared-successor
/// handoff needs the front's Clear -> Occupied transition) in the order it happened. Observation times
/// strictly increase, so the Track Controller never discards a step as a duplicate.
/// </para>
/// <para>
/// Dispatch: a <see cref="TrainDispatchRequestMessage"/> forwarded by the Track Controller is validated and
/// registered by the Track Service, and the Train Model is asked to activate the train
/// (<see cref="TrainActivationMessage"/>, on its own sender). Every outcome goes back to the Track Controller
/// as a <see cref="TrainDispatchStatusMessage"/>, which relays it to CTC. The train appears on the track only
/// when the Train Model's initial <see cref="TrainStateMessage"/> arrives; its footprint then follows each
/// position report. An undeliverable activation withdraws the placement (CTC retries it).
/// </para>
/// <para>
/// Feedback heartbeat: the Track Controller treats measurements older than its wall-clock watchdog (5 s)
/// as stale, and a healthy but unchanged track produces no state changes. So whenever nothing was sent to
/// the Track Controller for <see cref="DefaultFeedbackHeartbeat"/>, the current block states are
/// re-observed on the UI thread and sent with a fresh observation time. Current train environments and
/// direct traffic lights are retried too; layout and ticket-sales snapshots are not regenerated. The
/// heartbeat runs on wall time, independent of simulation speed and pause. If the Track Model stops or its
/// UI thread stops responding, no observation is taken and the Track Controller's feedback goes stale;
/// nothing is ever sent on the model's behalf.
/// </para>
/// </remarks>
public sealed class TrackModelConnection
{
    /// <summary>Heartbeat interval: well inside the Track Controller's 5 s feedback watchdog.</summary>
    public static readonly TimeSpan DefaultFeedbackHeartbeat = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long queued Track Controller observations are kept while it is unreachable. Its feedback
    /// watchdog is 5 s, so older observations could only ever be applied as stale; after this the backlog
    /// is replaced by one fresh full observation.
    /// </summary>
    public static readonly TimeSpan ControllerBacklogLifetime = TimeSpan.FromSeconds(5);

    /// <summary>Bound on queued Track Controller entries (a burst of occupancy steps while it is slow).</summary>
    public const int MaxQueuedControllerEntries = 512;

    private const string ReportedBy = "Track Model";

    private static readonly string[] DestinationNames = ["Track Controller", "Train Model", "CTC", "TestUI setup", "Train Controller (lights)"];

    private readonly ITrackService _track;
    private readonly Dispatcher _dispatcher;
    private readonly TrackModelEndpoints _endpoints;
    private readonly TrainControllerSignalPublisher _trainControllerSignals;
    private readonly TimeSpan _feedbackHeartbeat;
    private readonly Channel<Snapshot> _pending = Channel.CreateBounded<Snapshot>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    // Input results for the optional standalone TestUI. Sent off the input loop: connecting to an
    // absent pipe waits for the whole connect timeout, which must never delay the next input.
    private readonly Channel<TrackModelInputResultMessage> _testUiResults = Channel.CreateBounded<TrackModelInputResultMessage>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly DispatcherTimer _debounce;

    // Train environments for the Train Model's own sender (see ForwardTrainEnvironmentsAsync).
    private readonly Channel<List<TrackModelTrainEnvironmentMessage>> _trainModelPending = Channel.CreateBounded<List<TrackModelTrainEnvironmentMessage>>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    // Auxiliary inputs never hold up speed/authority delivery, even when a receiver is slow.
    private readonly Channel<TrainInputsSnapshot> _trainPassengerPending = Channel.CreateBounded<TrainInputsSnapshot>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    // Activation commands for the Train Model: few, ordered, never dropped.
    private readonly Channel<TrainActivationMessage> _activations = Channel.CreateUnbounded<TrainActivationMessage>(new UnboundedChannelOptions { SingleReader = true });

    // The single ordered Track Controller sender's queue (see EnqueueController).
    private readonly object _controllerGate = new();
    private readonly List<ControllerEntry> _controllerQueue = [];
    private readonly Channel<bool> _controllerWake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private bool _controllerResyncNeeded;

    // Lapses wayside speed/authority codes whose lease ran out (a silent Track Controller).
    private readonly DispatcherTimer _authorityLease;

    /// <summary>How often lapsed wayside authority leases are checked.</summary>
    public static readonly TimeSpan AuthorityLeaseCheckInterval = TimeSpan.FromMilliseconds(100);

    // UI thread only.
    private int _layoutRequestVersion;
    private Guid _latestSnapshotId;
    private DateTimeOffset _lastObservedAtUtc;
    private long _lastFullCaptureTimestamp = Stopwatch.GetTimestamp();
    private readonly HashSet<string> _deliveredActivations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastTelemetryRejection = new(StringComparer.Ordinal);

    // Orders train environments across snapshots and restarts (seeded from the clock). UI thread only.
    private long _environmentSequence = DateTime.UtcNow.Ticks;

    // Publisher loop only.
    private int _testUiLayoutRevision = -1;
    private int _testUiLayoutRequestVersion = -1;
    private int _ctcLayoutRevision = -1;
    private int _ctcLayoutRequestVersion = -1;
    private Snapshot? _latestPublished;
    private readonly bool?[] _delivery = new bool?[DestinationNames.Length];

    public event EventHandler<string>? StatusReported;
    public event EventHandler<string>? DeliveryReported;

    public TrackModelConnection(ITrackService track, Dispatcher dispatcher, TrackModelEndpoints? endpoints = null, TimeSpan? feedbackHeartbeat = null)
    {
        _track = track;
        _dispatcher = dispatcher;
        _endpoints = endpoints ?? TrackModelEndpoints.Production;
        _trainControllerSignals = new TrainControllerSignalPublisher(track, _endpoints.TrainController);
        _feedbackHeartbeat = feedbackHeartbeat ?? DefaultFeedbackHeartbeat;
        if (_feedbackHeartbeat <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(feedbackHeartbeat), "The feedback heartbeat must be positive.");
        _debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); QueueSnapshot(); };
        _track.StateChanged += (_, _) =>
        {
            _trainControllerSignals.Capture();
            _debounce.Stop();
            _debounce.Start();
        };
        _track.OccupancyTransition += (_, e) => OnOccupancyTransition(e);
        _track.TrainTrackingChanged += (_, e) => OnTrainTrackingChanged(e);
        _authorityLease = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = AuthorityLeaseCheckInterval };
        _authorityLease.Tick += (_, _) => ApplyThenPublishIfTrainCodesChanged(() => { _track.ExpireCommands(DateTimeOffset.UtcNow); });
    }

    /// <summary>Call on the UI thread. Publishes the initial snapshot (including the layout) and runs until cancelled.</summary>
    public Task RunAsync(CancellationToken token)
    {
        _authorityLease.Start();
        RequestSnapshot();
        return Task.WhenAll(
            NamedPipeTransport.ListenAsync(_endpoints.Inbound, HandleAsync,
                ex => Report("Inbound error: " + ex.Message), token),
            PublishAsync(token),
            ForwardToTrackControllerAsync(token),
            ForwardTrainEnvironmentsAsync(token),
            ForwardTrainPassengerInputsAsync(token),
            ForwardActivationsAsync(token),
            _trainControllerSignals.RunAsync(async delivered =>
            {
                lock (_delivery) _delivery[4] = delivered;
                await ReportDeliveryAsync(token);
            }, token),
            ForwardTestUiResultsAsync(token));
    }

    /// <summary>
    /// The Train Model's own sender: train environments, newest snapshot first and in order, never
    /// waiting behind a slow or absent CTC, Track Controller or TestUI pipe. Only the newest pending
    /// snapshot is kept, since each snapshot fully describes every train's current code.
    /// </summary>
    private async Task ForwardTrainEnvironmentsAsync(CancellationToken token)
    {
        try
        {
            await foreach (var environments in _trainModelPending.Reader.ReadAllAsync(token))
            {
                bool delivered = await SendBatchAsync(_endpoints.TrainModel, environments, token);
                bool changed;
                lock (_delivery) { changed = _delivery[1] != delivered; _delivery[1] = delivered; }
                if (changed) await ReportDeliveryAsync(token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task ForwardTrainPassengerInputsAsync(CancellationToken token)
    {
        var previousTrains = new HashSet<string>(StringComparer.Ordinal);
        var sentDemand = new Dictionary<string, (int Waiting, DateTimeOffset SentAt)>(StringComparer.Ordinal);
        var sentBeacons = new Dictionary<string, (string Payload, DateTimeOffset SentAt)>(StringComparer.Ordinal);
        // One beacon READ per pass over a beacon block: a new read when the train was not over a
        // beacon in the previous snapshot or the announced station changed.
        var beaconReads = new Dictionary<string, (string Station, long Sequence)>(StringComparer.Ordinal);
        var overBeacon = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await foreach (var snapshot in _trainPassengerPending.Reader.ReadAllAsync(token))
            {
                var current = snapshot.Passengers.Select(p => p.TrainId).ToHashSet(StringComparer.Ordinal);
                foreach (var departed in previousTrains.Except(current).ToList())
                {
                    var delivered = await TrySendAsync(_endpoints.TrainModel, new StationPassengerMessage { TrainId = departed }, token);
                    delivered &= await TrySendAsync(_endpoints.TrainModel, new BeaconMessage { TrainId = departed }, token);
                    if (delivered) { previousTrains.Remove(departed); sentDemand.Remove(departed); sentBeacons.Remove(departed); }
                }
                foreach (var passenger in snapshot.Passengers)
                {
                    previousTrains.Add(passenger.TrainId);
                    if (sentDemand.TryGetValue(passenger.TrainId, out var previous) && previous.Waiting == passenger.WaitingToBoard
                        && DateTimeOffset.UtcNow - previous.SentAt < DefaultFeedbackHeartbeat) continue;
                    if (await TrySendAsync(_endpoints.TrainModel, passenger, token))
                        sentDemand[passenger.TrainId] = (passenger.WaitingToBoard, DateTimeOffset.UtcNow);
                }
                var nowOverBeacon = new HashSet<string>(StringComparer.Ordinal);
                foreach (var beacon in snapshot.Beacons)
                {
                    nowOverBeacon.Add(beacon.TrainId);
                    beaconReads.TryGetValue(beacon.TrainId, out var read);
                    if (!overBeacon.Contains(beacon.TrainId) || read.Station != beacon.StationName)
                        read = (beacon.StationName, read.Sequence + 1);
                    beaconReads[beacon.TrainId] = read;
                    beacon.ReadSequence = read.Sequence;
                    var payload = MessageSerializer.Serialize(beacon);
                    if (sentBeacons.TryGetValue(beacon.TrainId, out var previous) && previous.Payload == payload
                        && DateTimeOffset.UtcNow - previous.SentAt < DefaultFeedbackHeartbeat) continue;
                    if (await TrySendAsync(_endpoints.TrainModel, beacon, token))
                        sentBeacons[beacon.TrainId] = (payload, DateTimeOffset.UtcNow);
                }
                overBeacon = nowOverBeacon;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>
    /// Activation and retirement commands for the Train Model, in order. The result of each delivery is
    /// applied on the UI thread: a delivered activation is reported as
    /// <see cref="TrainDispatchStage.ActivationRequested"/>; an undeliverable one withdraws the placement.
    /// </summary>
    private async Task ForwardActivationsAsync(CancellationToken token)
    {
        try
        {
            await foreach (var activation in _activations.Reader.ReadAllAsync(token))
            {
                // A longer connect window than the environments: the activation may queue behind a snapshot.
                bool delivered = await TrySendAsync(_endpoints.TrainModel, activation, token, connectTimeoutMilliseconds: 1000);
                await _dispatcher.InvokeAsync(() => OnActivationSent(activation, delivered), DispatcherPriority.Normal, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>Call on the UI thread. Publishes a full snapshot, re-sending the layout to every receiver.</summary>
    public void RequestSnapshot()
    {
        _layoutRequestVersion++;
        _debounce.Stop();
        QueueSnapshot();
    }

    public void Stop()
    {
        _debounce.Stop();
        _authorityLease.Stop();
    }

    /// <summary>
    /// Applies <paramref name="apply"/>, then publishes at once (no debounce) if any train moved to
    /// another block or its speed or authority code changed: a revocation, the code of the block a
    /// train just entered, and the occupancy the wayside needs for a handoff are never held back. UI thread only.
    /// </summary>
    private void ApplyThenPublishIfTrainCodesChanged(Action apply)
    {
        var before = _track.CaptureTrainCodes();
        apply();
        var after = _track.CaptureTrainCodes();
        if (before.Count != after.Count || after.Any(code => !before.TryGetValue(code.Key, out var previous) || previous != code.Value))
        {
            PublishNow();
        }
    }

    /// <summary>Publishes a snapshot at once, skipping the debounce (safety-relevant changes). UI thread only.</summary>
    private void PublishNow()
    {
        _debounce.Stop();
        QueueSnapshot();
    }

    private async Task HandleAsync(MessageEnvelope envelope)
    {
        // Position telemetry is a 10 Hz stream, not an operator input: it is not echoed to the TestUI.
        bool telemetry = envelope.MessageType == nameof(TrainStateMessage);
        var result = new TrackModelInputResultMessage { MessageType = envelope.MessageType };
        await _dispatcher.InvokeAsync(() =>
        {
            try
            {
                switch (envelope.MessageType)
                {
                    case nameof(TrackModelCommandMessage):
                        _track.ApplyCommand(MessageSerializer.DeserializePayload<TrackModelCommandMessage>(envelope)); break;
                    case nameof(TrackModelCommandBatchMessage):
                        var batch = MessageSerializer.DeserializePayload<TrackModelCommandBatchMessage>(envelope);
                        IReadOnlyList<string> refused = [];
                        ApplyThenPublishIfTrainCodesChanged(() => refused = _track.ApplyCommandBatch(batch, DateTimeOffset.UtcNow));
                        if (refused.Count > 0)
                            throw new InvalidOperationException($"{refused.Count} of {batch.Commands?.Count ?? 0} wayside commands refused: {string.Join("; ", refused.Take(3))}{(refused.Count > 3 ? "; …" : "")}");
                        break;
                    case nameof(TrackModelTrainUpdateMessage):
                        var update = MessageSerializer.DeserializePayload<TrackModelTrainUpdateMessage>(envelope);
                        ApplyThenPublishIfTrainCodesChanged(() => _track.ApplyTrainUpdate(update)); break;
                    case nameof(TrainStateMessage):
                        ApplyPosition(MessageSerializer.DeserializePayload<TrainStateMessage>(envelope));
                        return;
                    case nameof(TrainDispatchRequestMessage):
                        ApplyDispatch(MessageSerializer.DeserializePayload<TrainDispatchRequestMessage>(envelope)); break;
                    case nameof(TrainRouteUpdateMessage):
                        ApplyRouteUpdate(MessageSerializer.DeserializePayload<TrainRouteUpdateMessage>(envelope)); break;
                    case nameof(PassengerExchangeMessage):
                        var exchange = MessageSerializer.DeserializePayload<PassengerExchangeMessage>(envelope);
                        var applied = _track.ApplyPassengerExchange(exchange);
                        Log($"Train {exchange.TrainId} passenger exchange {(applied ? "recorded" : "unchanged")}: {exchange.Boarded} boarded, {exchange.Alighted} alighted, {exchange.LeftBehind} left behind.");
                        break;
                    case nameof(TrackModelFailureCommandMessage):
                        _track.ApplyFailures(MessageSerializer.DeserializePayload<TrackModelFailureCommandMessage>(envelope));
                        PublishNow(); break; // a fault is safety-relevant: no debounce before the wayside sees it
                    case nameof(SystemTimeMessage):
                        _track.SetSystemTime(MessageSerializer.DeserializePayload<SystemTimeMessage>(envelope).SystemTime); break;
                    case nameof(TrackModelTemperatureCommandMessage):
                        _track.ApplyTemperature(MessageSerializer.DeserializePayload<TrackModelTemperatureCommandMessage>(envelope)); break;
                    case nameof(TrackModelPassengerDemandMessage):
                        _track.ApplyPassengerDemand(MessageSerializer.DeserializePayload<TrackModelPassengerDemandMessage>(envelope)); break;
                    case nameof(MaintenanceRequestMessage):
                        var maintenance = MessageSerializer.DeserializePayload<MaintenanceRequestMessage>(envelope);
                        _track.SetMaintenance(maintenance.BlockId, maintenance.RequestedState); break;
                    case nameof(TrackModelSnapshotRequestMessage):
                        RequestSnapshot(); break;
                    default: throw new ArgumentException("Unsupported message type: " + envelope.MessageType);
                }
                StatusReported?.Invoke(this, "Input accepted");
                result.Accepted = true;
                result.Detail = "Input accepted.";
            }
            catch (Exception ex)
            {
                StatusReported?.Invoke(this, "Rejected: " + ex.Message);
                result.Detail = ex.Message;
            }
        });
        // This optional tester endpoint never changes delivery to the real modules.
        if (!telemetry) _testUiResults.Writer.TryWrite(result);
    }

    /// <summary>One Train Model position report. UI thread only.</summary>
    private void ApplyPosition(TrainStateMessage report)
    {
        TrainPositionResult result = new(TrainPositionOutcome.Rejected, string.Empty);
        ApplyThenPublishIfTrainCodesChanged(() => result = _track.ApplyTrainPosition(report));
        if (result.Outcome != TrainPositionOutcome.Rejected)
        {
            _lastTelemetryRejection.Remove(result.TrainId);
            return;
        }

        // Rejected telemetry is never applied; report each distinct reason once, not ten times a second.
        string key = result.TrainId.Length > 0 ? result.TrainId : report.TrainId ?? string.Empty;
        // The kind of rejection, without the numbers that change with every report.
        string reason = new(result.Detail.Where(c => !char.IsDigit(c)).ToArray());
        if (!_lastTelemetryRejection.TryGetValue(key, out var last) || last != reason)
        {
            _lastTelemetryRejection[key] = reason;
            Log("Position telemetry rejected: " + result.Detail);
        }
    }

    /// <summary>A dispatch forwarded by the Track Controller. UI thread only.</summary>
    private void ApplyDispatch(TrainDispatchRequestMessage request)
    {
        var placement = _track.RequestTrainPlacement(request);
        switch (placement.Outcome)
        {
            case TrainPlacementOutcome.Registered:
            case TrainPlacementOutcome.AlreadyPending:
                Log($"Dispatch {request.RequestId}: {placement.Detail}");
                _activations.Writer.TryWrite(new TrainActivationMessage
                {
                    DispatchRequestId = request.RequestId,
                    TrainId = request.TrainId,
                    LineId = request.LineId,
                    StartBlockId = request.StartBlockId,
                    Activate = true,
                });
                break;
            case TrainPlacementOutcome.AlreadyPlaced:
                Log($"Dispatch {request.RequestId}: {placement.Detail}");
                ReportStatus(request.RequestId, nameof(TrainDispatchRequestMessage), request.TrainId, request.LineId, request.StartBlockId, TrainDispatchStage.Placed, placement.Detail);
                break;
            default:
                Log($"Dispatch {request.RequestId} for train {request.TrainId} rejected: {placement.Detail}");
                ReportStatus(request.RequestId, nameof(TrainDispatchRequestMessage), request.TrainId, request.LineId, request.StartBlockId, TrainDispatchStage.Rejected, placement.Detail, placement.IsRetryable);
                break;
        }
    }

    /// <summary>A route update forwarded by the Track Controller. UI thread only.</summary>
    private void ApplyRouteUpdate(TrainRouteUpdateMessage update)
    {
        var result = _track.UpdateTrainRoute(update);
        bool accepted = result.Outcome == TrainPlacementOutcome.Registered;
        Log($"Route update {update.RequestId} for train {update.TrainId} {(accepted ? "accepted" : "rejected")}: {result.Detail}");
        ReportStatus(update.RequestId, nameof(TrainRouteUpdateMessage), update.TrainId, update.LineId, update.RouteBlockIds?.FirstOrDefault() ?? string.Empty,
            accepted ? TrainDispatchStage.RouteUpdated : TrainDispatchStage.Rejected, result.Detail, result.IsRetryable);
    }

    /// <summary>UI thread only.</summary>
    private void OnActivationSent(TrainActivationMessage activation, bool delivered)
    {
        if (!activation.Activate)
        {
            Log($"Train Model {(delivered ? "told" : "could not be told")} to retire train {activation.TrainId}.");
            return;
        }

        var tracked = _track.FindTrackedTrain(activation.TrainId);
        bool stillPending = tracked is { Status: TrainTrackingStatus.PendingPlacement } && tracked.DispatchRequestId == activation.DispatchRequestId;
        if (delivered)
        {
            _deliveredActivations.Add(activation.DispatchRequestId);
            if (stillPending)
            {
                ReportStatus(activation.DispatchRequestId, nameof(TrainDispatchRequestMessage), activation.TrainId, activation.LineId, activation.StartBlockId,
                    TrainDispatchStage.ActivationRequested, $"Train Model asked to activate train {activation.TrainId} at {activation.StartBlockId}; waiting for its initial position report.");
            }

            return;
        }

        if (stillPending)
        {
            // Not delivered: the train is not on the track and will not be; CTC retries the dispatch.
            _track.CancelPendingPlacement(activation.DispatchRequestId, "the Train Model is not reachable");
        }
    }

    /// <summary>Placement lifecycle from the Track Service, reported onward. UI thread only.</summary>
    private void OnTrainTrackingChanged(TrainTrackingEventArgs e)
    {
        Log($"Train {e.TrainId}: {e.Kind} — {e.Detail}.");
        switch (e.Kind)
        {
            case TrainTrackingEventKind.Placed:
                ReportStatus(e.DispatchRequestId, nameof(TrainDispatchRequestMessage), e.TrainId, e.LineId, e.StartBlockId, TrainDispatchStage.Placed, e.Detail);
                break;
            case TrainTrackingEventKind.Faulted:
                ReportStatus(e.DispatchRequestId, string.Empty, e.TrainId, e.LineId, e.StartBlockId, TrainDispatchStage.TrackingFault, e.Detail);
                break;
            case TrainTrackingEventKind.Retired:
                ReportStatus(e.DispatchRequestId, string.Empty, e.TrainId, e.LineId, e.StartBlockId, TrainDispatchStage.Retired, e.Detail);
                Deactivate(e);
                break;
            case TrainTrackingEventKind.PlacementWithdrawn:
                ReportStatus(e.DispatchRequestId, nameof(TrainDispatchRequestMessage), e.TrainId, e.LineId, e.StartBlockId, TrainDispatchStage.Rejected,
                    $"Placement withdrawn: {e.Detail}.", isRetryable: true);
                if (_deliveredActivations.Contains(e.DispatchRequestId)) Deactivate(e);
                break;
        }
    }

    private void Deactivate(TrainTrackingEventArgs e) => _activations.Writer.TryWrite(new TrainActivationMessage
    {
        DispatchRequestId = e.DispatchRequestId,
        TrainId = e.TrainId,
        LineId = e.LineId,
        StartBlockId = e.StartBlockId,
        Activate = false,
        Reason = e.Detail,
    });

    /// <summary>
    /// One atomic occupancy step: the line's block states exactly as they are now, as their own Track
    /// Controller observation (never merged with a later one). UI thread only, while the step is current.
    /// </summary>
    private void OnOccupancyTransition(TrackOccupancyTransitionEventArgs e)
    {
        EnqueueController([.. CaptureBlockStates(_latestSnapshotId, NextObservationTime(), e.LineId)], coalescible: false);
        string entered = e.Occupied.Count > 0 ? $" occupied {string.Join(", ", e.Occupied)}" : string.Empty;
        string cleared = e.Cleared.Count > 0 ? $" cleared {string.Join(", ", e.Cleared)}" : string.Empty;
        Log($"Train {e.TrainId}:{entered}{cleared}{(e.HeadBlockId.Length > 0 ? $" (front in {e.HeadBlockId})" : string.Empty)}.");
    }

    private void ReportStatus(string requestId, string requestType, string trainId, string lineId, string startBlockId, TrainDispatchStage stage, string detail, bool isRetryable = false) =>
        EnqueueController([new TrainDispatchStatusMessage
        {
            RequestId = requestId,
            RequestType = requestType,
            TrainId = trainId,
            LineId = lineId,
            StartBlockId = startBlockId,
            Stage = stage,
            IsRetryable = isRetryable,
            Detail = detail,
            ReportedBy = ReportedBy,
            ReportedAtUtc = DateTimeOffset.UtcNow,
        }], coalescible: false);

    private async Task ForwardTestUiResultsAsync(CancellationToken token)
    {
        try
        {
            await foreach (var result in _testUiResults.Reader.ReadAllAsync(token))
            {
                try { await NamedPipeTransport.SendAsync(_endpoints.TestUi, result, token, connectTimeoutMilliseconds: 100); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is TimeoutException or System.IO.IOException or UnauthorizedAccessException) { } // The standalone tester need not be running.
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void QueueSnapshot()
    {
        _trainControllerSignals.Capture(force: true);
        // Capture before leaving the UI thread; background sends never read mutable domain state.
        var snapshotId = Guid.NewGuid();
        _latestSnapshotId = snapshotId;
        var layout = _track.CreateLayoutMessage();
        layout.SnapshotId = snapshotId;
        EnqueueController([.. CaptureBlockStates(snapshotId, NextObservationTime())], coalescible: true);
        Interlocked.Exchange(ref _lastFullCaptureTimestamp, Stopwatch.GetTimestamp());
        // Head blocks first, so a train's own environment is never queued behind the whole network.
        var sequence = ++_environmentSequence;
        var environments = _track.Layout.Blocks.OrderBy(b => !(b.IsOccupied && b.IsTrainHead)).Select(b => _track.CreateTrainEnvironment(b.Id)).ToList();
        foreach (var environment in environments) { environment.SnapshotId = snapshotId; environment.Sequence = sequence; }
        QueueTrainInputs(environments);
        var sales = _track.Layout.Blocks.Select(b => b.LineId).Distinct().Select(_track.CreateTicketSales).ToList();
        _pending.Writer.TryWrite(new(_track.LayoutRevision, _layoutRequestVersion, layout,
            sales, new SystemTimeMessage { SystemTime = _track.SystemTime }));
    }

    /// <summary>
    /// Re-observes every block for the heartbeat. Runs on the UI thread, which owns the domain, so a
    /// stalled model yields no observation at all rather than a stale one with a new timestamp.
    /// The states keep the latest snapshot's ID (a newer snapshot, if any, follows immediately), so
    /// the TestUI keeps pairing layout, state and environment by snapshot.
    /// </summary>
    /// <remarks>
    /// Each train's head-block environment is re-sent with it (newer sequence, same values), so a Train
    /// Model that starts or reconnects receives its train's current code within a heartbeat instead of
    /// waiting for the next change.
    /// </remarks>
    private List<MessageEnvelope> CaptureFeedback()
    {
        _trainControllerSignals.Capture(force: true);
        var sequence = ++_environmentSequence;
        var environments = _track.Layout.Blocks.Where(b => b.IsTrainHead && b.TrainId.Length > 0).Select(b => _track.CreateTrainEnvironment(b.Id)).ToList();
        foreach (var environment in environments) { environment.SnapshotId = _latestSnapshotId; environment.Sequence = sequence; }
        QueueTrainInputs(environments);
        Interlocked.Exchange(ref _lastFullCaptureTimestamp, Stopwatch.GetTimestamp());
        return CaptureBlockStates(_latestSnapshotId, NextObservationTime());
    }

    /// <summary>
    /// Every block's measured state, as one Track Controller batch per line in layout order (or only
    /// <paramref name="lineId"/>'s). UI thread only.
    /// </summary>
    /// <param name="observedAt">One observation time for all blocks of the capture.</param>
    private List<MessageEnvelope> CaptureBlockStates(Guid snapshotId, DateTimeOffset observedAt, string? lineId = null) =>
        _track.Layout.Blocks.Where(b => lineId is null || b.LineId == lineId).GroupBy(b => b.LineId).Select(line => MessageSerializer.CreateEnvelope(BlockStatesBatchType, new
        {
            blocks = line.Select(b =>
            {
                var state = _track.CreateBlockState(b.Id);
                state.SnapshotId = snapshotId;
                state.ObservedAtUtc = observedAt;
                return state;
            }).ToList()
        })).ToList();

    /// <summary>
    /// Wall-clock observation time, strictly later than the previous one: the Track Controller ignores an
    /// observation that is not newer than the one it applied, so two steps captured in the same clock tick
    /// must still be ordered. UI thread only.
    /// </summary>
    private DateTimeOffset NextObservationTime()
    {
        var now = DateTimeOffset.UtcNow;
        _lastObservedAtUtc = now > _lastObservedAtUtc ? now : _lastObservedAtUtc.AddTicks(1);
        return _lastObservedAtUtc;
    }

    /// <summary>The Track Controller's batch feedback input: <c>{ "blocks": [TrackModelBlockStateMessage, ...] }</c>.</summary>
    public const string BlockStatesBatchType = "TrackModelBlockStates";

    /// <summary>
    /// Queues messages for the Track Controller, in order. A coalescible entry (a full snapshot) replaces
    /// an unsent coalescible entry at the tail: the newer one describes everything the older one did. An
    /// occupancy step or a dispatch status is never replaced or merged. Any thread.
    /// </summary>
    private void EnqueueController(IReadOnlyList<object> messages, bool coalescible)
    {
        if (messages.Count == 0) return;
        bool overflowed = false;
        lock (_controllerGate)
        {
            if (coalescible && _controllerQueue.Count > 0 && _controllerQueue[^1].Coalescible)
            {
                _controllerQueue[^1] = new ControllerEntry(messages, true);
            }
            else
            {
                _controllerQueue.Add(new ControllerEntry(messages, coalescible));
            }

            if (_controllerQueue.Count > MaxQueuedControllerEntries)
            {
                // The Track Controller is far behind: its oldest observations can only be stale by now.
                _controllerQueue.RemoveRange(0, _controllerQueue.Count - MaxQueuedControllerEntries / 2);
                _controllerResyncNeeded = true;
                overflowed = true;
            }
        }

        _controllerWake.Writer.TryWrite(true);
        if (overflowed) Report("Track Controller is not keeping up: older queued observations were dropped; a full observation follows.");
    }

    /// <summary>
    /// The single Track Controller sender: queued entries in order, and a fresh full observation (the
    /// heartbeat) whenever no full observation was taken for <see cref="DefaultFeedbackHeartbeat"/>, whether
    /// or not the Track Controller is reachable (the heartbeat also refreshes environments and lights).
    /// While the Track Controller is unreachable the queue is kept (it may only be busy) for at most
    /// <see cref="ControllerBacklogLifetime"/>, then replaced by one fresh observation.
    /// </summary>
    private async Task ForwardToTrackControllerAsync(CancellationToken token)
    {
        Stopwatch? failing = null;
        bool backingOff = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                bool resync;
                lock (_controllerGate) { resync = _controllerResyncNeeded; _controllerResyncNeeded = false; }
                var untilHeartbeat = _feedbackHeartbeat - SinceFullCapture;
                if (!resync && untilHeartbeat > TimeSpan.Zero && (backingOff || !HasControllerWork()))
                {
                    await WaitForControllerWorkAsync(untilHeartbeat, token);
                }

                if (resync || SinceFullCapture >= _feedbackHeartbeat)
                {
                    await _dispatcher.InvokeAsync(() => EnqueueController(CaptureFeedback(), coalescible: true), DispatcherPriority.Normal, token);
                }

                if (!HasControllerWork())
                {
                    backingOff = false;
                    continue;
                }

                bool delivered = await DrainControllerQueueAsync(token);
                backingOff = !delivered;
                if (delivered)
                {
                    failing = null;
                }
                else
                {
                    failing ??= Stopwatch.StartNew();
                    if (failing.Elapsed > ControllerBacklogLifetime)
                    {
                        // Only stale observations are left; the next heartbeat takes a fresh one.
                        lock (_controllerGate) { _controllerQueue.Clear(); }
                        failing = null;
                    }
                }

                bool changed;
                lock (_delivery) { changed = _delivery[0] != delivered; _delivery[0] = delivered; }
                if (changed) await ReportDeliveryAsync(token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>Wall time since the last full observation (snapshot or heartbeat) was queued.</summary>
    private TimeSpan SinceFullCapture => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastFullCaptureTimestamp));

    private bool HasControllerWork()
    {
        lock (_controllerGate) return _controllerQueue.Count > 0;
    }

    private async Task<bool> WaitForControllerWorkAsync(TimeSpan wait, CancellationToken token)
    {
        if (_controllerWake.Reader.TryRead(out _)) return true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(wait);
        try
        {
            await _controllerWake.Reader.ReadAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    /// <summary>Sends queued entries in order until the queue is empty (true) or a send fails (false; the entry stays queued).</summary>
    private async Task<bool> DrainControllerQueueAsync(CancellationToken token)
    {
        while (true)
        {
            ControllerEntry entry;
            lock (_controllerGate)
            {
                if (_controllerQueue.Count == 0) return true;
                entry = _controllerQueue[0];
            }

            foreach (var message in entry.Messages)
            {
                if (!await TrySendAsync(_endpoints.TrackController, message, token)) return false;
            }

            lock (_controllerGate)
            {
                // A coalescible head replaced while it was being sent is a newer entry: send it too.
                if (_controllerQueue.Count > 0 && ReferenceEquals(_controllerQueue[0], entry)) _controllerQueue.RemoveAt(0);
            }
        }
    }

    private async Task PublishAsync(CancellationToken token)
    {
        var sinceSnapshot = Stopwatch.StartNew();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var snapshot = await NextSnapshotAsync(_feedbackHeartbeat - sinceSnapshot.Elapsed, token);
                if (snapshot is not null)
                {
                    await PublishSnapshotAsync(snapshot, token);
                }
                else
                {
                    await PublishHeartbeatAsync(token);
                }

                sinceSnapshot.Restart();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>The next queued snapshot, or null once <paramref name="wait"/> passes without one.</summary>
    private async Task<Snapshot?> NextSnapshotAsync(TimeSpan wait, CancellationToken token)
    {
        if (_pending.Reader.TryRead(out var ready)) return ready;
        if (wait <= TimeSpan.Zero) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(wait);
        try { return await _pending.Reader.ReadAsync(timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    private async Task PublishSnapshotAsync(Snapshot snapshot, CancellationToken token)
    {
        _latestPublished = snapshot;
        // Layout/accepted time are private test setup on the TestUI endpoint.
        var testUi = new List<object>();
        if (snapshot.LayoutRequestVersion != _testUiLayoutRequestVersion || snapshot.Revision != _testUiLayoutRevision) testUi.Add(snapshot.Layout);
        testUi.Add(snapshot.Time);
        // Block states go through the ordered Track Controller sender; train environments have their own sender.
        var results = await Task.WhenAll(
            PublishToCtcAsync(snapshot, token),
            SendBatchAsync(_endpoints.TestUi, testUi, token));
        if (results[1])
        {
            _testUiLayoutRevision = snapshot.Revision;
            _testUiLayoutRequestVersion = snapshot.LayoutRequestVersion;
        }
        lock (_delivery) { _delivery[2] = results[0]; _delivery[3] = results[1]; }
        await ReportDeliveryAsync(token);
    }

    /// <summary>Heartbeat of the CTC side: a layout retry while CTC lacks it (block states have their own heartbeat).</summary>
    private async Task PublishHeartbeatAsync(CancellationToken token)
    {
        if (_latestPublished is not { } latest || !IsCtcLayoutPending(latest)) return;
        bool? previousCtc;
        lock (_delivery) previousCtc = _delivery[2];
        bool delivered = await SendCtcLayoutIfPendingAsync(latest, token);
        lock (_delivery) _delivery[2] = delivered;
        if (delivered != previousCtc) await ReportDeliveryAsync(token);
    }

    /// <summary>Layout first, so CTC knows the lines before it receives their ticket sales.</summary>
    private async Task<bool> PublishToCtcAsync(Snapshot snapshot, CancellationToken token) =>
        await SendCtcLayoutIfPendingAsync(snapshot, token)
        && await SendBatchAsync(_endpoints.Ctc, snapshot.Sales.Cast<object>(), token);

    private bool IsCtcLayoutPending(Snapshot snapshot) =>
        snapshot.Revision != _ctcLayoutRevision || snapshot.LayoutRequestVersion != _ctcLayoutRequestVersion;

    /// <summary>Sends the layout only when CTC has not received this revision/request yet; true when CTC has it.</summary>
    private async Task<bool> SendCtcLayoutIfPendingAsync(Snapshot snapshot, CancellationToken token)
    {
        if (!IsCtcLayoutPending(snapshot)) return true;
        if (!await SendBatchAsync(_endpoints.Ctc, [snapshot.Layout], token)) return false;
        _ctcLayoutRevision = snapshot.Revision;
        _ctcLayoutRequestVersion = snapshot.LayoutRequestVersion;
        return true;
    }

    private async Task ReportDeliveryAsync(CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        string text;
        lock (_delivery)
        {
            text = string.Join("  •  ", DestinationNames.Select((name, i) => name + ": " + _delivery[i] switch
            {
                true => "delivered",
                false => "offline",
                null => "pending",
            }));
        }
        await _dispatcher.InvokeAsync(() => DeliveryReported?.Invoke(this, text));
    }

    private static async Task<bool> SendBatchAsync(string pipe, IEnumerable<object> messages, CancellationToken token)
    {
        foreach (var message in messages)
        {
            if (!await TrySendAsync(pipe, message, token)) return false;
        }
        return true;
    }

    private static async Task<bool> TrySendAsync(string pipe, object message, CancellationToken token, int connectTimeoutMilliseconds = 250)
    {
        try { await NamedPipeTransport.SendAsync(pipe, message, token, connectTimeoutMilliseconds); return true; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    /// <summary>A dispatch/placement or occupancy event worth keeping: the status line and the debug log. UI thread only.</summary>
    private void Log(string text)
    {
        Trace.WriteLine($"[Track Model] {text}");
        StatusReported?.Invoke(this, text);
    }

    private void Report(string text) => _dispatcher.InvokeAsync(() => StatusReported?.Invoke(this, text));

    // Capture auxiliary inputs on the owning thread, just like the existing environment snapshots.
    private void QueueTrainInputs(List<TrackModelTrainEnvironmentMessage> environments)
    {
        _trainModelPending.Writer.TryWrite(environments);
        var passengers = environments.Where(e => e.TrainId.Length > 0).Select(e => new StationPassengerMessage
            { TrainId = e.TrainId, WaitingToBoard = e.WaitingPassengers }).ToList();
        var beacons = environments.Where(e => e.TrainId.Length > 0)
            .Select(e => _track.CreateTrainBeacon(e.BlockId)).OfType<BeaconMessage>().ToList();
        _trainPassengerPending.Writer.TryWrite(new(passengers, beacons));
    }

    private sealed record TrainInputsSnapshot(List<StationPassengerMessage> Passengers, List<BeaconMessage> Beacons);

    private sealed record ControllerEntry(IReadOnlyList<object> Messages, bool Coalescible);

    private sealed record Snapshot(int Revision, int LayoutRequestVersion, TrackLayoutMessage Layout,
        List<TicketSalesMessage> Sales, SystemTimeMessage Time);
}
