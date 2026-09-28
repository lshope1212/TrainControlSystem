namespace TrainModel.Core.Exceptions;

/// <summary>
/// Thrown when a train is asked to enter a state that is not physically valid.
/// Placeholder — not raised anywhere yet.
/// </summary>
public class InvalidTrainStateException : Exception
{
    public InvalidTrainStateException()
    {
    }

    public InvalidTrainStateException(string message)
        : base(message)
    {
    }

    public InvalidTrainStateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
