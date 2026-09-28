namespace TrainController.Core.Exceptions;

/// <summary>
/// Base exception for train controller faults. Placeholder — not thrown yet.
/// </summary>
public class TrainControllerException : Exception
{
    public TrainControllerException()
    {
    }

    public TrainControllerException(string message)
        : base(message)
    {
    }

    public TrainControllerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
