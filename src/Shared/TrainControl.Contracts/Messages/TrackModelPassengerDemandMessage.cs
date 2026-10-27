namespace TrainControl.Contracts.Messages;

/// <summary>Simulation input: replace a station's waiting population. Does not sell tickets.</summary>
public sealed class TrackModelPassengerDemandMessage
{
    public string BlockId { get; set; } = string.Empty;
    public int WaitingPassengers { get; set; }
}
