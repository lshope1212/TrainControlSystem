namespace TrainControl.Contracts.Messages;

/// <summary>
/// The wayside commands a Track Controller output in one evaluation, for many blocks, sent as a
/// single message. Direction: Track Controller -> Track Model.
/// </summary>
/// <remarks>
/// <para>
/// Each entry is an ordinary <see cref="TrackModelCommandMessage"/> and is validated and applied on
/// its own, in order: a command the Track Model refuses (for example throwing an occupied switch)
/// is reported and does not stop the others. Single <see cref="TrackModelCommandMessage"/>s remain
/// accepted (the Track Model TestUI sends them).
/// </para>
/// <para>
/// <see cref="Sequence"/> orders batches: the Track Model ignores, block by block, a command from a
/// batch whose sequence is not newer than the one it last applied to that block, so a late or replayed
/// batch can never restore withdrawn authority. A publisher seeds it from the wall clock when it
/// starts, so a restarted Track Controller is newer than any batch of its previous run.
/// </para>
/// <para>
/// <see cref="AuthorityLeaseSeconds"/> is how long the speed/authority codes in this batch stay valid
/// without a newer batch (the controller's own output validity). When it lapses the Track Model
/// replaces them with zero: a silent Track Controller never leaves authority on the track.
/// </para>
/// </remarks>
public sealed class TrackModelCommandBatchMessage
{
    public List<TrackModelCommandMessage> Commands { get; set; } = [];

    /// <summary>Monotonic batch number; 0 for an unordered (legacy/test) sender, applied as before.</summary>
    public long Sequence { get; set; }

    /// <summary>Validity of this batch's speed/authority codes after receipt, in seconds; 0 = no lease (legacy/test sender).</summary>
    public double AuthorityLeaseSeconds { get; set; }
}
