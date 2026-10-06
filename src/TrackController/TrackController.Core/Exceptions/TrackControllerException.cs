

namespace TrackController.Core.Exceptions;

/// <summary>
/// Base exception for wayside controller faults. Placeholder — not thrown yet.
/// </summary>
public class TrackControllerException : Exception
{
    /// <summary>Describes a controller-domain failure, optionally retaining its original cause.</summary>
    public TrackControllerException()
    {
    }

    /// <summary>Describes a controller-domain failure, optionally retaining its original cause.</summary>
    public TrackControllerException(string message) : base(message)
    {
    }

    /// <summary>Describes a controller-domain failure, optionally retaining its original cause.</summary>
    public TrackControllerException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
