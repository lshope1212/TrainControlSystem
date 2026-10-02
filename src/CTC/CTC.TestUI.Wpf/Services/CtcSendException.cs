namespace CTC.TestUI.Wpf.Services;

/// <summary>
/// Raised by an <see cref="ICtcMessageSender"/> when a message could not be delivered to
/// CTC. The message is readable by a developer, e.g. "CTC is not connected."
/// </summary>
public class CtcSendException : Exception
{
    public CtcSendException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
