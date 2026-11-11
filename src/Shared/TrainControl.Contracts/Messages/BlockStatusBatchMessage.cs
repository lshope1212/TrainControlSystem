namespace TrainControl.Contracts.Messages;

/// <summary>
/// One periodic wayside report covering many blocks: the <see cref="BlockStatusMessage"/>s a
/// Track Controller evaluated together, sent as a single message.
/// Direction: Track Controller -> CTC.
/// </summary>
/// <remarks>
/// <para>
/// A batch only changes how statuses travel, not what they mean: every entry is a complete
/// <see cref="BlockStatusMessage"/> with its own <see cref="BlockStatusMessage.ReportedAtUtc"/> and
/// optional movement decision, and a receiver applies the entries in order exactly as if each had
/// arrived on its own. One entry that cannot be applied (for example an unknown block ID) does not
/// prevent the others from being applied, and is reported rather than remapped.
/// </para>
/// <para>
/// Receivers that accept <see cref="BlockStatusMessage"/> keep accepting it (the CTC TestUI sends
/// single statuses); this batch exists because a full three-line network is 241 blocks reported
/// twice a second.
/// </para>
/// </remarks>
public class BlockStatusBatchMessage
{
    public List<BlockStatusMessage> Statuses { get; set; } = new List<BlockStatusMessage>();
}
