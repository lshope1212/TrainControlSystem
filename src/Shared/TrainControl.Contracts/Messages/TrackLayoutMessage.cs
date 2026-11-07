namespace TrainControl.Contracts.Messages;

/// <summary>
/// Transport-neutral description of the available lines and their topology.
/// Direction: Track Model -> CTC.
/// </summary>
/// <remarks>
/// <para>
/// The Track Model is the authority for block identity. Every
/// <see cref="TrackBlockDefinition.BlockId"/> is an opaque string that every module uses verbatim
/// (Track Model, Track Controller track maps, CTC) and that must be unique across ALL lines in
/// the integrated system, because block-level messages (e.g. <see cref="BlockStatusMessage"/>)
/// carry no line ID. Section letters and block numbers are separate display fields and are never
/// derived from, or parsed out of, the ID. When more than one line is loaded and plain numbers
/// would collide, the producer qualifies the IDs at the source (e.g. "GREEN-12"), and the Track
/// Controller track maps use the same IDs; no receiver remaps another module's IDs.
/// </para>
/// <para>
/// A message is the publisher's complete, authoritative set of lines. It is sent when the
/// publisher starts, when its layout changes and when a receiver asks for it
/// (<see cref="TrackModelSnapshotRequestMessage"/>); receivers must treat an equivalent repeated
/// layout as a no-op.
/// </para>
/// </remarks>
public class TrackLayoutMessage
{
    /// <summary>Identifies the matching Track Model state/environment snapshot; empty for legacy senders.</summary>
    public Guid SnapshotId { get; set; }

    public List<TrackLineDefinition> Lines { get; set; } = new List<TrackLineDefinition>();
}
