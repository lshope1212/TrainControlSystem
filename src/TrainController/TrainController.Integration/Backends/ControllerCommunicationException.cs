namespace TrainController.Integration.Backends;

/// <summary>
/// The backend could not obtain a trustworthy controller output (Raspberry Pi unreachable,
/// timeout, malformed / mismatched response, ...). The execution layer converts it into a
/// fail-safe output (power 0, emergency brake). It never triggers a Software fallback.
/// </summary>
public sealed class ControllerCommunicationException : Exception
{
    public ControllerCommunicationException(string message)
        : base(message)
    {
    }

    public ControllerCommunicationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
