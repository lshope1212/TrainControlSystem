namespace TrainModel.Core.Models;

/// <summary>
/// Placeholder snapshot of a train's current operating state.
/// </summary>
public class TrainState
{
    public string TrainId { get; set; } = string.Empty;

    public double PositionMeters { get; set; }

    public double VelocityMetersPerSecond { get; set; }

    public bool ServiceBrakeEngaged { get; set; }

    public bool EmergencyBrakeEngaged { get; set; }
}
