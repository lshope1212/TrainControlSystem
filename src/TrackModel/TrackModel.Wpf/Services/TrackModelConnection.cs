using System.Threading.Channels;
using System.Windows.Threading;
using TrackModel.Core.Interfaces;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Messages;

namespace TrackModel.Wpf.Services;

/// <summary>Infrastructure only: serializes mutations on the UI thread, then sends
/// immutable contract snapshots to the receiving module endpoints.</summary>
public sealed class TrackModelConnection
{
    private readonly ITrackService _track;
    private readonly Dispatcher _dispatcher;
    private readonly Channel<Snapshot> _pending = Channel.CreateBounded<Snapshot>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly DispatcherTimer _debounce;
    private int _layoutRequestVersion;
    private int _ctcLayoutRevision = -1;
    private int _ctcLayoutRequestVersion = -1;
    public event EventHandler<string>? StatusReported;
    public event EventHandler<string>? DeliveryReported;

    public TrackModelConnection(ITrackService track, Dispatcher dispatcher)
    {
        _track = track;
        _dispatcher = dispatcher;
        _debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); QueueSnapshot(); };
        _track.StateChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
    }

    public Task RunAsync(CancellationToken token)
    {
        RequestSnapshot();
        return Task.WhenAll(
            NamedPipeTransport.ListenAsync(NamedPipeNames.TrackModel, HandleAsync,
                ex => Report("Inbound error: " + ex.Message), token),
            PublishAsync(token));
    }

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
                    case nameof(TrackModelTrainUpdateMessage):
                        _track.ApplyTrainUpdate(MessageSerializer.DeserializePayload<TrackModelTrainUpdateMessage>(envelope)); break;
                    case nameof(TrackModelFailureCommandMessage):
                        _track.ApplyFailures(MessageSerializer.DeserializePayload<TrackModelFailureCommandMessage>(envelope)); break;
                    case nameof(SystemTimeMessage):
                        _track.SetSystemTime(MessageSerializer.DeserializePayload<SystemTimeMessage>(envelope).SystemTime); break;
                    case nameof(TrackModelTemperatureCommandMessage):
                        _track.ApplyTemperature(MessageSerializer.DeserializePayload<TrackModelTemperatureCommandMessage>(envelope)); break;
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
        try { await NamedPipeTransport.SendAsync(NamedPipeNames.TrackModelTestUi, result, connectTimeoutMilliseconds: 100); }
        catch (TimeoutException) { } // The standalone tester need not be running.
        catch (System.IO.IOException) { }
    }

    private void QueueSnapshot()
    {
        // Capture before leaving the UI thread; background sends never read mutable domain state.
        var snapshotId = Guid.NewGuid();
        var layout = _track.CreateLayoutMessage();
        layout.SnapshotId = snapshotId;
        var states = _track.Layout.Blocks.Select(b => _track.CreateBlockState(b.Id)).ToList();
        var environments = _track.Layout.Blocks.Select(b => _track.CreateTrainEnvironment(b.Id)).ToList();
        foreach (var state in states) state.SnapshotId = snapshotId;
        foreach (var environment in environments) environment.SnapshotId = snapshotId;
        var signals = environments.Select(e => new TrackModelSignalMessage
            { BlockId = e.BlockId, TrainId = e.TrainId, Signal = e.Signal }).ToList();
        var sales = _track.Layout.Blocks.Select(b => b.LineId).Distinct().Select(_track.CreateTicketSales).ToList();
        _pending.Writer.TryWrite(new(_track.LayoutRevision, _layoutRequestVersion, layout,
            states, environments, signals, sales));
    }

    private async Task PublishAsync(CancellationToken token)
    {
        try
        {
            await foreach (var snapshot in _pending.Reader.ReadAllAsync(token))
            {
                var ctc = new List<object>();
                if (snapshot.LayoutRequestVersion != _ctcLayoutRequestVersion || snapshot.Revision != _ctcLayoutRevision) ctc.Add(snapshot.Layout);
                ctc.AddRange(snapshot.Sales);
                var results = await Task.WhenAll(
                    SendBatchAsync(NamedPipeNames.TrackController, snapshot.Blocks.Cast<object>(), token),
                    SendBatchAsync(NamedPipeNames.TrainModel, snapshot.Environments.Cast<object>(), token),
                    SendBatchAsync(NamedPipeNames.TrainController, snapshot.Signals.Cast<object>(), token),
                    SendBatchAsync(NamedPipeNames.Ctc, ctc, token));
                if (results[3])
                {
                    _ctcLayoutRevision = snapshot.Revision;
                    _ctcLayoutRequestVersion = snapshot.LayoutRequestVersion;
                }
                if (!token.IsCancellationRequested)
                    await _dispatcher.InvokeAsync(() => DeliveryReported?.Invoke(this,
                        string.Join("  •  ", new[] { "Track Controller", "Train Model", "Train Controller", "CTC" }
                            .Select((name, i) => name + ": " + (results[i] ? "delivered" : "offline")))));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
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
        List<TrackModelBlockStateMessage> Blocks, List<TrackModelTrainEnvironmentMessage> Environments,
        List<TrackModelSignalMessage> Signals, List<TicketSalesMessage> Sales);
}
