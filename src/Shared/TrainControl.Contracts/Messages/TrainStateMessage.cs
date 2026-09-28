namespace TrainControl.Contracts.Messages;

/// <summary>
/// Placeholder message describing the current physical state of a train.
/// Published by the Train Model, consumed by controllers and the CTC office.
/// </summary>
public class TrainStateMessage
{
    public string TrainId { get; set; } = string.Empty;

    public double PositionMeters { get; set; }

    public double SpeedMetersPerSecond { get; set; }

    public double AccelerationMetersPerSecondSquared { get; set; }
}
