using System.Collections.Concurrent;
using TrackController.Contracts.Messages;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Messages;

namespace TrackController.TestUI.Wpf.Services;

/// <summary>
/// Fake peer modules, not a fake controller. This class has no Core reference: sends are real pipe
/// messages and observations come exclusively from the module output and diagnostic pipes.
/// </summary>
public sealed class WaysideTestConnection : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly object _observations = new();
    private readonly ConcurrentQueue<MessageEnvelope> _recent = new();
    private Task[]? _listeners;
    private WaysideSnapshotMessage? _snapshot;
    private DateTimeOffset _resetAt;
    private long _sent, _ctcReceived, _trackModelReceived;

    public (string Input, string CtcOutput, string TrackModelOutput) Endpoints { get; }

    public event Action<string>? StatusReceived;

    public event Action<WaysideAcknowledgementMessage>? AcknowledgementReceived;

    public WaysideSnapshotMessage? LatestSnapshot
    {
        get
        {
            lock (_observations)
            {
                return _snapshot;
            }
        }
    }

    public bool IsSnapshotCurrent => LatestSnapshot is { } snapshot && snapshot.ValidUntilUtc > DateTimeOffset.UtcNow;

    public long SentCount => Interlocked.Read(ref _sent);

    public long CtcReceivedCount => Interlocked.Read(ref _ctcReceived);

    public long TrackModelReceivedCount => Interlocked.Read(ref _trackModelReceived);

    public IReadOnlyList<MessageEnvelope> RecentMessages => _recent.ToArray();

    /// <summary>Creates fake peer endpoints that observe outputs only through named pipes.</summary>
    public WaysideTestConnection(string inputPipe, string ctcOutput, string trackModelOutput)
    {
        Endpoints = (inputPipe, ctcOutput, trackModelOutput);
    }

    /// <summary>Starts fake CTC and Track Model listeners to capture actual controller outputs.</summary>
    public void Start()
    {
        if (_listeners is not null)
        {
            throw new InvalidOperationException("Output listeners already started.");
        }

        _listeners =
        [
            NamedPipeTransport.ListenAsync(Endpoints.CtcOutput, envelope => CaptureAsync(envelope, true), ReportError, _stop.Token),
            NamedPipeTransport.ListenAsync(Endpoints.TrackModelOutput, envelope => CaptureAsync(envelope, false), ReportError, _stop.Token),
            NamedPipeTransport.ListenAsync(Endpoints.Input + ".Monitor", envelope => CaptureAsync(envelope, null), ReportError, _stop.Token)
        ];
    }

    /// <summary>Sends an application message using the same serializer and named-pipe transport as live modules.</summary>
    public async Task SendAsync(object message, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        if (message is IReadOnlyList<TrackModelBlockStateMessage> blocks)
        {
            message = MessageSerializer.CreateEnvelope("TrackModelBlockStates", new { blocks });
        }
        await NamedPipeTransport.SendAsync(Endpoints.Input, message, linked.Token);
        Interlocked.Increment(ref _sent);
    }

    private Task CaptureAsync(MessageEnvelope envelope, bool? ctc)
    {
        if (ctc == true)
        {
            Interlocked.Increment(ref _ctcReceived);
        }
        else if (ctc == false)
        {
            Interlocked.Increment(ref _trackModelReceived);
        }

        _recent.Enqueue(envelope);
        while (_recent.Count > 100)
        {
            _recent.TryDequeue(out _);
        }

        if (ctc is null && envelope.MessageType == nameof(WaysideSnapshotMessage))
        {
            var snapshot = MessageSerializer.DeserializePayload<WaysideSnapshotMessage>(envelope);
            lock (_observations)
            {
                if (snapshot.EvaluatedAtUtc >= _resetAt && (_snapshot is null || snapshot.EvaluatedAtUtc >= _snapshot.EvaluatedAtUtc))
                {
                    _snapshot = snapshot;
                }
            }
        }
        else if (ctc is null && envelope.MessageType == nameof(WaysideAcknowledgementMessage))
        {
            var result = MessageSerializer.DeserializePayload<WaysideAcknowledgementMessage>(envelope);
            AcknowledgementReceived?.Invoke(result);
        }

        return Task.CompletedTask;
    }

    private void ReportError(Exception ex) => StatusReceived?.Invoke($"Output connection: {ex.Message}");

    /// <summary>Discards captured outputs so a reset cannot display results from the previous test session.</summary>
    public void ClearObservations()
    {
        lock (_observations)
        {
            _resetAt = DateTimeOffset.UtcNow;
            _snapshot = null;
        }

        _recent.Clear();
        Interlocked.Exchange(ref _sent, 0);
        Interlocked.Exchange(ref _ctcReceived, 0);
        Interlocked.Exchange(ref _trackModelReceived, 0);
    }

    /// <summary>Stops owned background operations and releases connections before returning.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_listeners is not null)
        {
            await Task.WhenAll(_listeners);
        }

        _stop.Dispose();
    }
}
