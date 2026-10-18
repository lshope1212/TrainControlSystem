namespace CTC.Core.Exceptions;

/// <summary>
/// Raised when CTC cannot release a queued train yet (e.g. its start block is occupied or
/// closed; the train stays queued for retry), or cannot apply a dispatcher's change to a
/// released train's movement plan (e.g. no route to the destination; the old plan is kept).
/// The message is dispatcher-readable.
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
