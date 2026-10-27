namespace TrainModel.TestUI.Wpf.Services;

/// <summary>
/// Sends a shared-contract message into the running Train Model process, the way a
/// real neighbour (Train Controller, Track Model) would. Delivery is not an
/// acknowledgement that the Train Model acted on the message.
/// </summary>
public interface ITrainModelMessageSender
{
    Task SendToTrainModelAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : class;
}
