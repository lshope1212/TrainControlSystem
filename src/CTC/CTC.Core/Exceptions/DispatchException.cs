namespace CTC.Core.Exceptions;

/// <summary>
/// Raised when a dispatch request cannot be honored. Placeholder — not thrown yet.
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
