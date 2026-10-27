namespace TrainControl.Contracts.Messages;

/// <summary>
/// Simulation-only station demand delivered from the Track Model to the Train
/// Model: how many passengers are waiting to board at the approaching station.
/// </summary>
public class StationPassengerMessage
{
    public string TrainId { get; set; } = string.Empty;

    /// <summary>Passengers waiting on the platform who may board.</summary>
    public int WaitingToBoard { get; set; }
}
