using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;

namespace TrackController.Contracts.Messages;
/// <summary>Enables maintenance controls while withholding automatic movement.</summary>
public sealed class WaysideMaintenanceModeMessage
{
    [JsonPropertyName("enabled")]
    public bool IsEnabled { get; set; }
}
