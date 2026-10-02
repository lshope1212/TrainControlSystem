using System.IO;
using CTC.Core.Exceptions;
using CTC.Core.Interfaces;
using TrainControl.Common.Communication;

namespace CTC.Wpf.Services;

/// <summary>
/// Sends CTC's outgoing messages to the Track Controller process over a local Windows
/// named pipe, as one newline-delimited JSON envelope per connection
/// (format: <see cref="MessageSerializer"/>; transport: <see cref="NamedPipeTransport"/>).
/// </summary>
/// <remarks>
/// A new connection is opened for every message; there is no persistent connection
/// yet. Successful delivery only means the bytes reached the pipe server, not that the
/// Track Controller accepted or acted on the request. During development the pipe server
/// may be CTC.TestUI.Wpf standing in for the Track Controller; this class cannot tell.
/// </remarks>
public sealed class NamedPipeMessageSender : IMessageSender
{
    public async Task SendAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            await NamedPipeTransport.SendAsync(NamedPipeNames.TrackController, message, cancellationToken);
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
}
