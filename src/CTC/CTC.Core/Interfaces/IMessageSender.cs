namespace CTC.Core.Interfaces;

/// <summary>
/// Sends an outgoing shared-contract message to another subsystem.
/// CTC.Core decides WHAT to send; implementations (in the WPF/infrastructure layer)
/// decide HOW it is physically transported and serialized.
/// </summary>
public interface IMessageSender
{
    /// <summary>
    /// Completes when the message has been handed off to the transport. This is NOT
    /// an acknowledgement that the receiving subsystem acted on it.
    /// </summary>
    /// <exception cref="Exceptions.MessageSendException">The message could not be delivered.</exception>
    Task SendAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : class;
}
