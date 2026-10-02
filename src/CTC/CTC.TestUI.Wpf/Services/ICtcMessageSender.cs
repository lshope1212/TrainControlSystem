namespace CTC.TestUI.Wpf.Services;

/// <summary>
/// Sends a shared-contract message into the running CTC process, the way an external
/// module (Track Controller, Track Model, system clock) would.
/// </summary>
public interface ICtcMessageSender
{
    /// <summary>
    /// Completes when the message has been delivered to CTC's inbound endpoint. This is
    /// NOT an acknowledgement that CTC accepted or acted on it.
    /// </summary>
    /// <exception cref="CtcSendException">The message could not be delivered.</exception>
    Task SendToCtcAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : class;
}
