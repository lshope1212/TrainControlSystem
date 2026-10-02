using System.IO;
using TrainControl.Common.Communication;

namespace CTC.TestUI.Wpf.Services;

/// <summary>
/// Delivers simulated module messages to CTC's inbound pipe (<see cref="NamedPipeNames.Ctc"/>)
/// using the same envelope and transport the real modules will use.
/// </summary>
public sealed class NamedPipeCtcMessageSender : ICtcMessageSender
{
    public async Task SendToCtcAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            await NamedPipeTransport.SendAsync(NamedPipeNames.Ctc, message, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new CtcSendException("CTC is not connected.", ex);
        }
        catch (IOException ex)
        {
            throw new CtcSendException("The connection to CTC failed while sending.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new CtcSendException("Access to the CTC pipe was denied.", ex);
        }
    }
}
