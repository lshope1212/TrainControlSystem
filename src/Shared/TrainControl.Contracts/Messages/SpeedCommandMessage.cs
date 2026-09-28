namespace TrainControl.Contracts.Messages;

/// <summary>
/// Placeholder message carrying a commanded speed for a single train.
/// </summary>
public class SpeedCommandMessage
{
    public string TrainId { get; set; } = string.Empty;

    public double CommandedSpeedMetersPerSecond { get; set; }
}
