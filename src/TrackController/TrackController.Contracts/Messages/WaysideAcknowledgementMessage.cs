using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;

namespace TrackController.Contracts.Messages;
/// <summary>Application result published on the diagnostic monitor; transport delivery alone is not acceptance.</summary>
public sealed record WaysideAcknowledgementMessage(
    [property: JsonPropertyName("accepted")] bool IsAccepted,
    string Detail,
    string MessageType = "")
{
    /// <summary>
    /// True when an accepted movement request was only staged as a successor behind its predecessor.
    /// Staging grants nothing; CTC is told <see cref="RequestAcknowledgementOutcome.PreparedAsSuccessor"/>.
    /// </summary>
    [JsonPropertyName("stagedSuccessor")]
    public bool IsStagedSuccessor { get; init; }

    /// <summary>The movement request already ended. It no longer grants authority.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsRetiredMovement { get; init; }
}
