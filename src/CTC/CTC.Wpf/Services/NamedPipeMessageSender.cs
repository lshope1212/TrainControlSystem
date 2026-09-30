using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CTC.Core.Exceptions;
using CTC.Core.Interfaces;

namespace CTC.Wpf.Services;

/// <summary>
/// Sends CTC's outgoing messages to the Track Controller process over a local Windows
/// named pipe, as one newline-delimited JSON envelope per connection.
/// </summary>
/// <remarks>
/// A new connection is opened for every message; there is no persistent connection
/// yet. Successful delivery only means the bytes reached the pipe server, not that the
/// Track Controller accepted or acted on the request.
/// </remarks>
public sealed class NamedPipeMessageSender : IMessageSender
{
    /// <summary>Pipe the Track Controller process is expected to listen on.</summary>
    public const string TrackControllerPipeName = "TrainControl.TrackController";

    private const string LocalServer = ".";
    private const int ConnectTimeoutMilliseconds = 2500;

    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task SendAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(message);

        var json = Serialize(message);

        try
        {
            await using var pipe = new NamedPipeClientStream(LocalServer, TrackControllerPipeName, PipeDirection.Out, PipeOptions.Asynchronous);

            await pipe.ConnectAsync(ConnectTimeoutMilliseconds, cancellationToken);

            await using var writer = new StreamWriter(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new MessageSendException("Track Controller is not connected.", ex);
        }
        catch (IOException ex)
        {
            throw new MessageSendException("The connection to Track Controller failed while sending.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MessageSendException("Access to the Track Controller pipe was denied.", ex);
        }
    }

    /// <summary>
    /// Wraps the message in an envelope naming its contract type, e.g.
    /// {"messageType":"MaintenanceRequestMessage","payload":{"blockId":"12","requestedState":"Closed"}}
    /// </summary>
    internal static string Serialize(object message) => JsonSerializer.Serialize(new MessageEnvelope(message.GetType().Name, message), SerializerOptions);

    // Transport framing only; deliberately kept out of CTC.Core and the shared contracts.
    // Payload is typed as object so it is serialized using its runtime contract type.
    private sealed record MessageEnvelope(string MessageType, object Payload);
}
