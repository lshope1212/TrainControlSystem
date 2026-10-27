namespace TrainControl.Contracts.Messages;

/// <summary>
/// Passenger exchange reported from the Train Model to the Track Model at a stop:
/// how many boarded and how many alighted. The Track Model decides who boards
/// (platform demand); the Train Model decides who alights (onboard load).
/// </summary>
public class PassengerExchangeMessage
{
    public string TrainId { get; set; } = string.Empty;

    public int Boarded { get; set; }

    public int Alighted { get; set; }

    /// <summary>Passengers who could not board because the train was at capacity.
    /// The Track Model keeps these on the platform.</summary>
    public int LeftBehind { get; set; }
}
