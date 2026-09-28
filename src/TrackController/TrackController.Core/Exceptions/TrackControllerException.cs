namespace TrackController.Core.Exceptions;

/// <summary>
/// Base exception for wayside controller faults. Placeholder — not thrown yet.
/// </summary>
public class TrackControllerException : Exception
{
    public TrackControllerException()
    {
    }

    public TrackControllerException(string message)
        : base(message)
    {
    }

    public TrackControllerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
