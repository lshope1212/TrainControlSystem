using System.IO;
using TrainControl.Common.Communication;

namespace TrainModel.TestUI.Wpf.Services;

/// <summary>
/// Delivers simulated neighbour messages to the Train Model's inbound pipe
/// (<see cref="NamedPipeNames.TrainModel"/>) using the shared envelope + transport.
/// </summary>
public sealed class NamedPipeTrainModelMessageSender : ITrainModelMessageSender
{
    public async Task SendToTrainModelAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            await NamedPipeTransport.SendAsync(NamedPipeNames.TrainModel, message, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new TrainModelSendException("Train Model is not connected.", ex);
        }
        catch (IOException ex)
        {
            throw new TrainModelSendException("The connection to the Train Model failed while sending.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new TrainModelSendException("Access to the Train Model pipe was denied.", ex);
        }
    }
}
