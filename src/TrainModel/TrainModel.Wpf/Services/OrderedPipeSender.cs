using System.Threading.Channels;
using TrainControl.Common.Communication;

namespace TrainModel.Wpf.Services;

/// <summary>
/// One ordered, bounded send queue per destination pipe. Replaces "fire and forget" sends
/// (one Task.Run per message), which made many clients compete for the single-instance pipe
/// server at once: messages queued for seconds and arrived out of order. Here a single
/// consumer per pipe sends in order; when a neighbour is slow or absent, the oldest
/// queued messages are dropped instead of piling up.
/// </summary>
public sealed class OrderedPipeSender
{
    /// <summary>Short connect timeout: a missing neighbour must not hold the queue for seconds.</summary>
    public const int ConnectTimeoutMilliseconds = 250;

    private const int Capacity = 512;
    private readonly Dictionary<string, Channel<object>> _queues = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public void Enqueue(string pipeName, object message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Channel<object> queue;
        lock (_gate)
        {
            if (!_queues.TryGetValue(pipeName, out queue!))
            {
                queue = Channel.CreateBounded<object>(new BoundedChannelOptions(Capacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                });
                _queues[pipeName] = queue;
                _ = Task.Run(() => DrainAsync(pipeName, queue.Reader));
            }
        }

        queue.Writer.TryWrite(message);
    }

    private static async Task DrainAsync(string pipeName, ChannelReader<object> reader)
    {
        await foreach (var message in reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await NamedPipeTransport.SendAsync(pipeName, message, CancellationToken.None, ConnectTimeoutMilliseconds).ConfigureAwait(false);
            }
            catch
            {
                // No listener / transient failure: drop this message. The train runs standalone
                // whether or not a neighbour (or the TestUI) is listening.
            }
        }
    }
}
