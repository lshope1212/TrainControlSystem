using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;

namespace TrackController.Contracts.Messages;

/// <summary>
/// Authority is measured from the specified front position along TargetBlockId + PathBlockIds.
/// Repeated status reports retain this origin; receivers must subtract traveled distance,
/// never restart the distance on receipt. TrainId is a CTC label, not inferred from occupancy.
/// </summary>
public sealed class WaysideMovementRequestMessage
{
    public string RequestId { get; set; } = "";

    public string TrainId { get; set; } = "";

    public string TargetBlockId { get; set; } = "";

    public string? EntryBlockId { get; set; }

    public double? FrontOffsetMeters { get; set; }

    public List<string> PathBlockIds { get; set; } = [];

    public double SuggestedSpeedMetersPerSecond { get; set; }

    public double SuggestedAuthorityMeters { get; set; }

    [JsonPropertyName("active")]
    public bool IsActive { get; set; } = true;
}
