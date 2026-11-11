namespace TrainControl.Contracts.Messages;

/// <summary>
/// The wayside commands a Track Controller output in one evaluation, for many blocks, sent as a
/// single message. Direction: Track Controller -> Track Model.
/// </summary>
/// <remarks>
/// Each entry is an ordinary <see cref="TrackModelCommandMessage"/> and is validated and applied on
/// its own, in order: a command the Track Model refuses (for example throwing an occupied switch)
/// is reported and does not stop the others. Single <see cref="TrackModelCommandMessage"/>s remain
/// accepted (the Track Model TestUI sends them).
/// </remarks>
public sealed class TrackModelCommandBatchMessage
{
    public List<TrackModelCommandMessage> Commands { get; set; } = [];
}
