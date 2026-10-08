using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;

namespace TrackController.Contracts.Messages;

/// <summary>Selects PLC or software logic for a local controller.</summary>
public sealed class WaysideControllerModeMessage
{
    [JsonPropertyName("hardwareMode")]
    public bool IsHardwareMode { get; set; } = true;
}
