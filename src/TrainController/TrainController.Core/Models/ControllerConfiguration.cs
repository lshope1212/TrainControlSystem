namespace TrainController.Core.Models;

/// <summary>
/// Placeholder tuning/configuration values for the train controller.
/// Real control gains are not chosen yet.
/// </summary>
public class ControllerConfiguration
{
    public double ProportionalGain { get; set; }

    public double IntegralGain { get; set; }

    public double MaxSpeedMetersPerSecond { get; set; }
}
