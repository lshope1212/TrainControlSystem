using TrainControl.Contracts.Enums;

namespace TrainControl.Contracts.Messages;

/// <summary>Wayside commands for one track block. All physical quantities use SI units.</summary>
/// <remarks>
/// <para>
/// Speed and authority are the track-circuit code the wayside puts on this block. Authority is
/// measured from this block's entry toward the fixed end point of the movement it belongs to
/// (<see cref="MovementRequestId"/>). Zero speed and authority is always an authoritative stop code:
/// no receiver may treat it as "no update".
/// </para>
/// <para>
/// With an empty <see cref="AuthorityEntryBlockId"/> the code is for the train occupying the block
/// (the movement's target). With <see cref="AuthorityEntryBlockId"/> set, the block is the next block
/// of a movement whose successor is prepared, and the code is that same movement's authority
/// expressed from this block's entry (never more): it applies only to a train that entered this block
/// from <see cref="AuthorityEntryBlockId"/>; any other occupant receives zero.
/// </para>
/// </remarks>
public sealed class TrackModelCommandMessage
{
    public string BlockId { get; set; } = string.Empty;
    public double CommandedSpeedMetersPerSecond { get; set; }
    public double AuthorityMeters { get; set; }
    public SwitchPosition Switch { get; set; } = SwitchPosition.Normal;
    public SignalState Signal { get; set; } = SignalState.Green;
    public CrossingState Crossing { get; set; } = CrossingState.Open;

    /// <summary>The Track Controller movement request this code belongs to; empty when no movement is coded.</summary>
    public string MovementRequestId { get; set; } = string.Empty;

    /// <summary>For a continuation code: the block a train must have entered from for the code to apply; empty otherwise.</summary>
    public string AuthorityEntryBlockId { get; set; } = string.Empty;
}
