using System.IO.Pipes;
using System.Text;

namespace TrainControl.Common.Communication;

/// <summary>
/// Minimal local named-pipe transport: one connection per message, carrying a single
/// newline-terminated <see cref="MessageSerializer"/> envelope. No persistent connection
/// and no acknowledgement: a completed send only means the bytes reached the pipe server.
/// </summary>
public static class NamedPipeTransport
{
    private const string LocalServer = ".";
    private const int DefaultConnectTimeoutMilliseconds = 2500;

    // Back-off after a listener-side transport fault, so a persistent fault (e.g. a second
    // copy of the process already owns the pipe) cannot turn the loop into a busy spin.
    private static readonly TimeSpan ListenerFaultDelay = TimeSpan.FromSeconds(1);

    /// <summary>Connects to <paramref name="pipeName"/> and writes one envelope.</summary>
    /// <exception cref="TimeoutException">No server is listening on the pipe (receiver not running).</exception>
    /// <exception cref="IOException">The connection failed while sending.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the pipe was denied.</exception>
    public static async Task SendAsync(
        string pipeName,
        object message,
        CancellationToken cancellationToken = default,
        int connectTimeoutMilliseconds = DefaultConnectTimeoutMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(message);

        var json = MessageSerializer.Serialize(message);

        await using var pipe = new NamedPipeClientStream(LocalServer, pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(connectTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);

        await using var writer = new StreamWriter(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Hosts the pipe server for <paramref name="pipeName"/> until cancelled, accepting one
    /// connection at a time and passing each received envelope to <paramref name="onMessage"/>.
    /// </summary>
    /// <remarks>
    /// Callbacks run on thread-pool threads; callers must marshal to their UI thread.
    /// A malformed message, a failing handler or a broken connection is reported through
    /// <paramref name="onError"/> and never ends the loop. Completes when cancelled.
    /// </remarks>
    public static async Task ListenAsync(
        string pipeName,
        Func<MessageEnvelope, Task> onMessage,
        Action<Exception> onError,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onMessage);
        ArgumentNullException.ThrowIfNull(onError);

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;

            try
            {
                await using var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                onError(ex);
                await DelayQuietlyAsync(ListenerFaultDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                onError(new InvalidDataException("A client connected but sent no message."));
                continue;
            }

            try
            {
                await onMessage(MessageSerializer.Deserialize(line)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                onError(ex);
            }
        }
    }

    private static async Task DelayQuietlyAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
