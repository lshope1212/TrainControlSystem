namespace TrainModel.Core.Models;

/// <summary>
/// Pure domain model for a single train. No UI, no WPF, no I/O.
/// Placeholder properties only.
/// </summary>
public class Train
{
    public string Id { get; set; } = string.Empty;

    public double MassKg { get; set; }

    public double PositionMeters { get; set; }

    public double VelocityMetersPerSecond { get; set; }

    public double AccelerationMetersPerSecondSquared { get; set; }
}
