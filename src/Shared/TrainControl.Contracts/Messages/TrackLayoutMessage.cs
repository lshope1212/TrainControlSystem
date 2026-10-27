namespace TrainControl.Contracts.Messages;

/// <summary>
/// Transport-neutral description of the available lines and their topology.
/// Direction: Track Model -> CTC.
/// </summary>
public class TrackLayoutMessage
{
    /// <summary>Identifies the matching Track Model state/environment snapshot; empty for legacy senders.</summary>
    public Guid SnapshotId { get; set; }

    public List<TrackLineDefinition> Lines { get; set; } = new List<TrackLineDefinition>();
}
