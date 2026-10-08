using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;

namespace TrackController.Contracts.Messages;

/// <summary>Requests pause or speed changes without changing feedback watchdog timing.</summary>
public sealed class WaysideSimulationMessage
{
    [JsonPropertyName("paused")]
    public bool IsPaused { get; set; } = true;

    public double SpeedMultiplier { get; set; } = 1;
}
