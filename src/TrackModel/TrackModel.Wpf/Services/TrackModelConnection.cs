using System.Diagnostics;
using System.Threading.Channels;
using System.Windows.Threading;
using TrackModel.Core.Interfaces;
using TrainControl.Common.Communication;
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
/// Outputs: block states to the Track Controller, train environments to the Train Model, the
/// layout and ticket sales to CTC, per-train light states to the Train Controller, and private
/// setup to the standalone TestUI. Lights have their own sender and update immediately on state
/// changes, on snapshot requests and on heartbeat. A state change
/// publishes one full snapshot (debounced). The layout goes to CTC only when CTC does not have
/// the current revision yet: on start-up, after a layout change, after an explicit
/// <see cref="TrackModelSnapshotRequestMessage"/>, and again on every heartbeat while CTC has
/// not received it (e.g. CTC started after the Track Model).
/// </para>
/// <para>
/// Block states go to the Track Controller as one <c>TrackModelBlockStates</c> batch per line
/// (the Track Controller's existing batch input), not one connection per block: the three course
/// lines are 241 blocks. A Track Controller that does not own a line rejects that line's batch
/// only. Each state keeps its own observation time. Inputs are applied one message at a time on
/// the UI thread; a <see cref="TrackModelCommandBatchMessage"/> applies its commands one by one,
/// so one refused command does not hold back the others.
/// </para>
/// <para>
/// Feedback heartbeat: the Track Controller treats measurements older than its wall-clock
/// watchdog (5 s) as stale, and a healthy but unchanged track produces no state changes. So
/// whenever no snapshot was published for <see cref="DefaultFeedbackHeartbeat"/>, the current
/// block states are re-observed on the UI thread and sent to the Track Controller with a
/// fresh observation time. Current train environments and direct traffic lights are retried too;
/// layout and ticket-sales snapshots are not regenerated.
/// The heartbeat runs on wall time, independent of simulation speed and pause. If the Track
/// Model stops or its UI thread stops responding, no observation is taken and the Track
/// Controller's feedback goes stale; nothing is ever sent on the model's behalf.
/// </para>
/// </remarks>
public sealed class TrackModelConnection
{
    /// <summary>Heartbeat interval: well inside the Track Controller's 5 s feedback watchdog.</summary>
    public static readonly TimeSpan DefaultFeedbackHeartbeat = TimeSpan.FromSeconds(1);

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

    // Lapses wayside speed/authority codes whose lease ran out (a silent Track Controller).
    private readonly DispatcherTimer _authorityLease;

    /// <summary>How often lapsed wayside authority leases are checked.</summary>
    public static readonly TimeSpan AuthorityLeaseCheckInterval = TimeSpan.FromMilliseconds(100);

    // UI thread only.
    private int _layoutRequestVersion;
    private Guid _latestSnapshotId;

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
            ForwardTrainEnvironmentsAsync(token),
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
        _testUiResults.Writer.TryWrite(result);
    }

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
        var feedback = CaptureBlockStates(snapshotId, observedAt: null);
        // Occupied blocks first, so a train's own environment is never queued behind the whole network.
        var sequence = ++_environmentSequence;
        var environments = _track.Layout.Blocks.OrderBy(b => b.TrainId.Length == 0).Select(b => _track.CreateTrainEnvironment(b.Id)).ToList();
        foreach (var environment in environments) { environment.SnapshotId = snapshotId; environment.Sequence = sequence; }
        _trainModelPending.Writer.TryWrite(environments);
        var sales = _track.Layout.Blocks.Select(b => b.LineId).Distinct().Select(_track.CreateTicketSales).ToList();
        _pending.Writer.TryWrite(new(_track.LayoutRevision, _layoutRequestVersion, layout,
            feedback, sales, new SystemTimeMessage { SystemTime = _track.SystemTime }));
    }

    /// <summary>
    /// Re-observes every block for the heartbeat. Runs on the UI thread, which owns the domain, so a
    /// stalled model yields no observation at all rather than a stale one with a new timestamp.
    /// The states keep the latest snapshot's ID (a newer snapshot, if any, follows immediately), so
    /// the TestUI keeps pairing layout, state and environment by snapshot.
    /// </summary>
    /// <remarks>
    /// The occupied blocks' train environments are re-sent with it (newer sequence, same values), so a
    /// Train Model that starts or reconnects receives its train's current code within a heartbeat
    /// instead of waiting for the next change.
    /// </remarks>
    private List<MessageEnvelope> CaptureFeedback()
    {
        _trainControllerSignals.Capture(force: true);
        var sequence = ++_environmentSequence;
        var environments = _track.Layout.Blocks.Where(b => b.TrainId.Length > 0).Select(b => _track.CreateTrainEnvironment(b.Id)).ToList();
        foreach (var environment in environments) { environment.SnapshotId = _latestSnapshotId; environment.Sequence = sequence; }
        if (environments.Count > 0) _trainModelPending.Writer.TryWrite(environments);
        return CaptureBlockStates(_latestSnapshotId, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Every block's measured state, as one Track Controller batch per line in layout order. UI thread only.
    /// </summary>
    /// <param name="observedAt">One observation time for all blocks; null keeps each state's creation time.</param>
    private List<MessageEnvelope> CaptureBlockStates(Guid snapshotId, DateTimeOffset? observedAt) =>
        _track.Layout.Blocks.GroupBy(b => b.LineId).Select(line => MessageSerializer.CreateEnvelope(BlockStatesBatchType, new
        {
            blocks = line.Select(b =>
            {
                var state = _track.CreateBlockState(b.Id);
                state.SnapshotId = snapshotId;
                if (observedAt is { } time) state.ObservedAtUtc = time;
                return state;
            }).ToList()
        })).ToList();

    /// <summary>The Track Controller's batch feedback input: <c>{ "blocks": [TrackModelBlockStateMessage, ...] }</c>.</summary>
    public const string BlockStatesBatchType = "TrackModelBlockStates";

    private async Task PublishAsync(CancellationToken token)
    {
        var sinceFeedback = Stopwatch.StartNew();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var snapshot = await NextSnapshotAsync(_feedbackHeartbeat - sinceFeedback.Elapsed, token);
                if (snapshot is not null)
                {
                    await PublishSnapshotAsync(snapshot, token);
                }
                else
                {
                    await PublishHeartbeatAsync(token);
                }

                // Both paths sent (or attempted) fresh block states to the Track Controller.
                sinceFeedback.Restart();
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
        // Train environments have their own sender (ForwardTrainEnvironmentsAsync).
        var results = await Task.WhenAll(
            SendBatchAsync(_endpoints.TrackController, snapshot.Feedback, token),
            PublishToCtcAsync(snapshot, token),
            SendBatchAsync(_endpoints.TestUi, testUi, token));
        if (results[2])
        {
            _testUiLayoutRevision = snapshot.Revision;
            _testUiLayoutRequestVersion = snapshot.LayoutRequestVersion;
        }
        lock (_delivery) { _delivery[0] = results[0]; _delivery[2] = results[1]; _delivery[3] = results[2]; }
        await ReportDeliveryAsync(token);
    }

    /// <summary>Heartbeat: fresh block states to the Track Controller, plus a layout retry while CTC lacks it.</summary>
    private async Task PublishHeartbeatAsync(CancellationToken token)
    {
        var feedback = await _dispatcher.InvokeAsync(CaptureFeedback, DispatcherPriority.Normal, token);
        var layoutRetry = _latestPublished is { } latest && IsCtcLayoutPending(latest) ? latest : null;

        var toController = SendBatchAsync(_endpoints.TrackController, feedback, token);
        var toCtc = layoutRetry is null ? null : SendCtcLayoutIfPendingAsync(layoutRetry, token);

        bool? previousController = _delivery[0], previousCtc = _delivery[2];
        _delivery[0] = await toController;
        if (toCtc is not null) _delivery[2] = await toCtc;
        if (_delivery[0] != previousController || _delivery[2] != previousCtc) await ReportDeliveryAsync(token);
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
        string text = string.Join("  •  ", DestinationNames.Select((name, i) => name + ": " + _delivery[i] switch
        {
            true => "delivered",
            false => "offline",
            null => "pending",
        }));
        await _dispatcher.InvokeAsync(() => DeliveryReported?.Invoke(this, text));
    }

    private static async Task<bool> SendBatchAsync(string pipe, IEnumerable<object> messages, CancellationToken token)
    {
        foreach (var message in messages)
        {
            try { await NamedPipeTransport.SendAsync(pipe, message, token, connectTimeoutMilliseconds: 250); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { return false; }
        }
        return true;
    }

    private void Report(string text) => _dispatcher.InvokeAsync(() => StatusReported?.Invoke(this, text));
    private sealed record Snapshot(int Revision, int LayoutRequestVersion, TrackLayoutMessage Layout,
        List<MessageEnvelope> Feedback,
        List<TicketSalesMessage> Sales, SystemTimeMessage Time);
}
