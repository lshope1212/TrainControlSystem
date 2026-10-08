using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;

namespace TrackController.Contracts.Messages;

/// <summary>
/// Authority defines a fixed route limit from the entry of TargetBlockId along TargetBlockId + PathBlockIds.
/// It is not remaining distance from the train. The receiver must locate the train relative to that limit.
/// TrainId is a CTC label, not inferred from occupancy.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class WaysideMovementRequestMessage
{
    public const string BlockEntryReference = "TargetBlockEntry";

    public string RequestId { get; set; } = "";

    public string TrainId { get; set; } = "";

    public string TargetBlockId { get; set; } = "";

    public string? EntryBlockId { get; set; }

    public string AuthorityReference { get; set; } = BlockEntryReference;

    public List<string> PathBlockIds { get; set; } = [];

    public double SuggestedSpeedMetersPerSecond { get; set; }

    public double SuggestedAuthorityMeters { get; set; }

    [JsonPropertyName("active")]
    public bool IsActive { get; set; } = true;
}
