namespace CTC.Core.Exceptions;

/// <summary>
/// Raised when a queued train cannot be released yet (e.g. its start block is occupied or
/// closed). The message is dispatcher-readable; the train stays queued for retry.
/// </summary>
public class DispatchException : Exception
{
    public DispatchException()
    {
    }

    public DispatchException(string message)
        : base(message)
    {
    }

    public DispatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
