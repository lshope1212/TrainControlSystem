namespace TrainController.Core.Models;

/// <summary>
/// Placeholder snapshot of what the train controller currently believes and commands.
/// </summary>
public class ControllerState
{
    public string TrainId { get; set; } = string.Empty;

    public double CommandedSpeedMetersPerSecond { get; set; }

    public double AuthorityMeters { get; set; }

    public bool ServiceBrakeRequested { get; set; }

    public bool EmergencyBrakeRequested { get; set; }
}
