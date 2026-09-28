namespace TrackModel.Core.Exceptions;

/// <summary>
/// Thrown when a track layout is structurally invalid. Placeholder — not raised yet.
/// </summary>
public class InvalidTrackLayoutException : Exception
{
    public InvalidTrackLayoutException()
    {
    }

    public InvalidTrackLayoutException(string message)
        : base(message)
    {
    }

    public InvalidTrackLayoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
