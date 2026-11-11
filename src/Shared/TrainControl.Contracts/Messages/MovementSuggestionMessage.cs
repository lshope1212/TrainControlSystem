using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;

namespace TrainControl.Contracts.Messages;

/// <summary>
/// Authority defines a fixed route limit from the entry of TargetBlockId along TargetBlockId + PathBlockIds.
/// It is not remaining distance from the train. The receiver must locate the train relative to that limit.
/// TrainId is a CTC label, not inferred from occupancy.
/// </summary>
/// <remarks>
/// <para>
/// A request with <see cref="PredecessorRequestId"/> is a <em>successor</em>: CTC prepares it while the
/// train still occupies the predecessor's target block, so that authority can change hands without an
/// interruption when the train moves on. The Track Controller only validates and stages it; a staged
/// successor grants nothing, takes part in no route-conflict check and is never commanded to the track.
/// The Track Controller promotes it (and retires the predecessor in the same step) only when fresh
/// occupancy shows the train moving from <see cref="EntryBlockId"/> into <see cref="TargetBlockId"/>
/// while the predecessor's own grant covered <see cref="TargetBlockId"/>. Its acknowledgement is
/// <see cref="RequestAcknowledgementOutcome.PreparedAsSuccessor"/>, which is never a grant.
/// </para>
/// <para>
/// A successor expresses the predecessor's fixed end point from the new block: its target is the
/// predecessor's first path block, its entry is the predecessor's target, its path continues the
/// predecessor's path (never beyond it), and its authority is at most the predecessor's authority less
/// the predecessor target's length. Its speed never exceeds the predecessor's.
/// </para>
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MovementSuggestionMessage
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

    /// <summary>
    /// For a successor request: the active request it is prepared to take over from. Null for an
    /// ordinary request (the meaning of every existing message is unchanged).
    /// </summary>
    public string? PredecessorRequestId { get; set; }
}
