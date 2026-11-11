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
    public static TrackModelEndpoints Production { get; } = new(
        NamedPipeNames.TrackModel, NamedPipeNames.TrackController, NamedPipeNames.TrainModel, NamedPipeNames.Ctc, NamedPipeNames.TrackModelTestUi);
}

/// <summary>Infrastructure only: serializes mutations on the UI thread, then sends
/// immutable contract snapshots to the receiving module endpoints.</summary>
/// <remarks>
/// <para>
/// Outputs: block states to the Track Controller, train environments to the Train Model, the
/// layout and ticket sales to CTC, and private setup to the standalone TestUI. A state change
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
/// block states are re-observed on the UI thread and sent to the Track Controller only, with a
/// fresh observation time. Nothing else (layout, environments, ticket sales) is regenerated.
/// The heartbeat runs on wall time, independent of simulation speed and pause. If the Track
/// Model stops or its UI thread stops responding, no observation is taken and the Track
/// Controller's feedback goes stale; nothing is ever sent on the model's behalf.
/// </para>
/// </remarks>
public sealed class TrackModelConnection
{
    /// <summary>Heartbeat interval: well inside the Track Controller's 5 s feedback watchdog.</summary>
    public static readonly TimeSpan DefaultFeedbackHeartbeat = TimeSpan.FromSeconds(1);

    private static readonly string[] DestinationNames = ["Track Controller", "Train Model", "CTC", "TestUI setup"];

    private readonly ITrackService _track;
    private readonly Dispatcher _dispatcher;
    private readonly TrackModelEndpoints _endpoints;
    private readonly TimeSpan _feedbackHeartbeat;
    private readonly Channel<Snapshot> _pending = Channel.CreateBounded<Snapshot>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    // Input results for the optional standalone TestUI. Sent off the input loop: connecting to an
    // absent pipe waits for the whole connect timeout, which must never delay the next input.
    private readonly Channel<TrackModelInputResultMessage> _testUiResults = Channel.CreateBounded<TrackModelInputResultMessage>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly DispatcherTimer _debounce;

    // UI thread only.
    private int _layoutRequestVersion;
    private Guid _latestSnapshotId;

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
        _feedbackHeartbeat = feedbackHeartbeat ?? DefaultFeedbackHeartbeat;
        if (_feedbackHeartbeat <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(feedbackHeartbeat), "The feedback heartbeat must be positive.");
        _debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); QueueSnapshot(); };
        _track.StateChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
    }

    /// <summary>Call on the UI thread. Publishes the initial snapshot (including the layout) and runs until cancelled.</summary>
    public Task RunAsync(CancellationToken token)
    {
        RequestSnapshot();
        return Task.WhenAll(
            NamedPipeTransport.ListenAsync(_endpoints.Inbound, HandleAsync,
                ex => Report("Inbound error: " + ex.Message), token),
            PublishAsync(token),
            ForwardTestUiResultsAsync(token));
    }

    /// <summary>Call on the UI thread. Publishes a full snapshot, re-sending the layout to every receiver.</summary>
    public void RequestSnapshot()
    {
        _layoutRequestVersion++;
        _debounce.Stop();
        QueueSnapshot();
    }

    public void Stop() => _debounce.Stop();

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
                        var commands = MessageSerializer.DeserializePayload<TrackModelCommandBatchMessage>(envelope).Commands ?? [];
                        var refused = _track.ApplyCommands(commands);
                        if (refused.Count > 0)
                            throw new InvalidOperationException($"{refused.Count} of {commands.Count} wayside commands refused: {string.Join("; ", refused.Take(3))}{(refused.Count > 3 ? "; …" : "")}");
                        break;
                    case nameof(TrackModelTrainUpdateMessage):
                        _track.ApplyTrainUpdate(MessageSerializer.DeserializePayload<TrackModelTrainUpdateMessage>(envelope)); break;
                    case nameof(TrackModelFailureCommandMessage):
                        _track.ApplyFailures(MessageSerializer.DeserializePayload<TrackModelFailureCommandMessage>(envelope)); break;
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
        // Capture before leaving the UI thread; background sends never read mutable domain state.
        var snapshotId = Guid.NewGuid();
        _latestSnapshotId = snapshotId;
        var layout = _track.CreateLayoutMessage();
        layout.SnapshotId = snapshotId;
        var feedback = CaptureBlockStates(snapshotId, observedAt: null);
        var environments = _track.Layout.Blocks.Select(b => _track.CreateTrainEnvironment(b.Id)).ToList();
        foreach (var environment in environments) environment.SnapshotId = snapshotId;
        var sales = _track.Layout.Blocks.Select(b => b.LineId).Distinct().Select(_track.CreateTicketSales).ToList();
        _pending.Writer.TryWrite(new(_track.LayoutRevision, _layoutRequestVersion, layout,
            feedback, environments, sales, new SystemTimeMessage { SystemTime = _track.SystemTime }));
    }

    /// <summary>
    /// Re-observes every block for the heartbeat. Runs on the UI thread, which owns the domain, so a
    /// stalled model yields no observation at all rather than a stale one with a new timestamp.
    /// The states keep the latest snapshot's ID (a newer snapshot, if any, follows immediately), so
    /// the TestUI keeps pairing layout, state and environment by snapshot.
    /// </summary>
    private List<MessageEnvelope> CaptureFeedback() => CaptureBlockStates(_latestSnapshotId, DateTimeOffset.UtcNow);

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
        var results = await Task.WhenAll(
            SendBatchAsync(_endpoints.TrackController, snapshot.Feedback, token),
            SendBatchAsync(_endpoints.TrainModel, snapshot.Environments.Cast<object>(), token),
            PublishToCtcAsync(snapshot, token),
            SendBatchAsync(_endpoints.TestUi, testUi, token));
        if (results[3])
        {
            _testUiLayoutRevision = snapshot.Revision;
            _testUiLayoutRequestVersion = snapshot.LayoutRequestVersion;
        }
        for (int i = 0; i < results.Length; i++) _delivery[i] = results[i];
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
        List<MessageEnvelope> Feedback, List<TrackModelTrainEnvironmentMessage> Environments,
        List<TicketSalesMessage> Sales, SystemTimeMessage Time);
}
