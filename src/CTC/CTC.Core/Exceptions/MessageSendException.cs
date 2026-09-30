namespace CTC.Core.Exceptions;

/// <summary>
/// Raised by an <see cref="Interfaces.IMessageSender"/> when an outgoing message could
/// not be delivered (e.g. the receiving subsystem is not running). Transport-neutral:
/// the message should be readable by a dispatcher.
/// </summary>
public class MessageSendException : Exception
{
    public MessageSendException()
    {
    }

    public MessageSendException(string message)
        : base(message)
    {
    }

    public MessageSendException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
