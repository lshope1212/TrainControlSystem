using System.Threading.Channels;
using TrackModel.Core.Interfaces;
using TrainControl.Common.Communication;
using TrainControl.Common.Identity;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackModel.Wpf.Services;

/// <summary>
/// Sends each occupying train its current block's effective light state using the existing
/// Train Controller contract. Capture runs on the model's UI thread; sending has its own queue
/// so an absent Train Controller cannot delay wayside feedback or train environments.
/// </summary>
internal sealed class TrainControllerSignalPublisher(ITrackService track, string endpoint)
{
    private readonly Channel<List<TrackModelSignalMessage>> _pending = Channel.CreateBounded<List<TrackModelSignalMessage>>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private List<TrackModelSignalMessage> _lastCaptured = [];

    // Sender thread only. Retain every attempted train until its clearing message succeeds,
    // including across a disconnect or a dropped pending snapshot.
    private readonly Dictionary<string, string> _sentTrainBlocks = new(StringComparer.Ordinal);

    public void Capture(bool force = false)
    {
        var signals = track.Layout.Blocks
            .Where(block => block.IsOccupied && TrainIdConvention.TryNormalize(block.TrainId, out _))
            .Select(block => new TrackModelSignalMessage
            {
                BlockId = block.Id,
                TrainId = Normalize(block.TrainId),
                Signal = block.EffectiveSignal
            })
            .GroupBy(signal => signal.TrainId, StringComparer.Ordinal)
            .Select(group => group.Count() == 1 ? group.Single() : new TrackModelSignalMessage
            {
                TrainId = group.Key,
                Signal = SignalState.Unknown // Conflicting aliases must not select a light arbitrarily.
            })
            .OrderBy(signal => signal.TrainId, StringComparer.Ordinal)
            .ToList();

        if (!force && signals.Select(Key).SequenceEqual(_lastCaptured.Select(Key))) return;
        _lastCaptured = signals;
        _pending.Writer.TryWrite(signals);
    }

    public async Task RunAsync(Func<bool, Task> reportDelivery, CancellationToken token)
    {
        bool? previousDelivery = null;
        try
        {
            await foreach (var signals in _pending.Reader.ReadAllAsync(token))
            {
                if (signals.Count == 0 && _sentTrainBlocks.Count == 0) continue;
                var delivered = await SendAsync(signals, token);
                if (delivered != previousDelivery)
                {
                    previousDelivery = delivered;
                    await reportDelivery(delivered);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task<bool> SendAsync(List<TrackModelSignalMessage> signals, CancellationToken token)
    {
        var currentTrains = signals.Select(signal => signal.TrainId).ToHashSet(StringComparer.Ordinal);
        var removed = _sentTrainBlocks.Where(pair => !currentTrains.Contains(pair.Key)).ToArray();
        foreach (var (trainId, blockId) in removed)
        {
            if (!await TrySendAsync(new TrackModelSignalMessage
                { TrainId = trainId, BlockId = blockId, Signal = SignalState.Unknown }, token)) return false;
            _sentTrainBlocks.Remove(trainId);
        }

        foreach (var signal in signals)
        {
            _sentTrainBlocks[signal.TrainId] = signal.BlockId;
            if (!await TrySendAsync(signal, token)) return false;
        }
        return true;
    }

    private async Task<bool> TrySendAsync(TrackModelSignalMessage signal, CancellationToken token)
    {
        try
        {
            await NamedPipeTransport.SendAsync(endpoint, signal, token, connectTimeoutMilliseconds: 250);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is TimeoutException or System.IO.IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static (string TrainId, string BlockId, SignalState Signal) Key(TrackModelSignalMessage signal) =>
        (signal.TrainId, signal.BlockId, signal.Signal);

    private static string Normalize(string trainId)
    {
        TrainIdConvention.TryNormalize(trainId, out var canonical);
        return canonical;
    }
}
