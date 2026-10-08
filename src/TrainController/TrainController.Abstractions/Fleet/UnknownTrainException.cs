namespace TrainController.Abstractions.Fleet;

/// <summary>
/// Thrown when a TrainId is not one of the ten fixed fleet trains. Unknown trains are
/// never silently routed to a default controller.
/// </summary>
public sealed class UnknownTrainException : ArgumentException
{
    public UnknownTrainException(string? trainId)
        : base($"Unknown TrainId '{trainId ?? "<null>"}'. Valid trains are {string.Join(", ", TrainFleet.AllTrainIds)}.")
    {
        TrainId = trainId;
    }

    public string? TrainId { get; }
}
