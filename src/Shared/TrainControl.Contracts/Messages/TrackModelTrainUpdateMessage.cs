namespace TrainControl.Contracts.Messages;

/// <summary>Train telemetry and a passenger exchange at its current block.
/// An empty CurrentBlockId removes the train from the track. ExchangeId makes station
/// exchanges idempotent when a sender retries the same operation.</summary>
public sealed class TrackModelTrainUpdateMessage
{
    public string TrainId { get; set; } = string.Empty;
    public string CurrentBlockId { get; set; } = string.Empty;
    public double ActualSpeedMetersPerSecond { get; set; }
    public int BoardingPassengers { get; set; }
    public int DisembarkingPassengers { get; set; }
    public string ExchangeId { get; set; } = string.Empty;
}
