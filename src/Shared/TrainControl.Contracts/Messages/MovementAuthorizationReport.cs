using TrainControl.Contracts.Enums;

namespace TrainControl.Contracts.Messages;

/// <summary>
/// The Track Controller's current decision on one movement request, carried on the status of the
/// request's target block (<see cref="BlockStatusMessage.Movement"/>).
/// Direction: Track Controller -> CTC.
/// </summary>
/// <remarks>
/// This is the wayside's ACTUAL authorization: what it commands to the track, after its own
/// safety checks. It is distinct from the request merely being accepted for evaluation (see
/// <see cref="RequestAcknowledgementMessage"/>), and from what CTC suggested.
/// </remarks>
public sealed class MovementAuthorizationReport
{
    /// <summary>The <see cref="MovementSuggestionMessage.RequestId"/> this decision belongs to.</summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary>CTC's train label from the request; informational only.</summary>
    public string TrainId { get; set; } = string.Empty;

    public MovementAuthorizationState State { get; set; } = MovementAuthorizationState.None;

    /// <summary>Speed the wayside actually commands; 0 unless <see cref="State"/> grants movement.</summary>
    public double AuthorizedSpeedMetersPerSecond { get; set; }

    /// <summary>
    /// Authority the wayside actually commands, measured like the request's: from the entry of the
    /// target block. 0 unless <see cref="State"/> grants movement.
    /// </summary>
    public double AuthorizedAuthorityMeters { get; set; }

    /// <summary>Readable explanation, e.g. why authority was withheld or restricted.</summary>
    public string Reason { get; set; } = string.Empty;
}
