using TrainControl.Contracts.Messages;

namespace CTC.Core.Models;

/// <summary>
/// Placeholder aggregate view of the whole railway as the CTC office sees it.
/// Built from shared contract messages, never from other subsystems' internal types.
/// </summary>
public class SystemState
{
    public IList<TrainStateMessage> Trains { get; } = new List<TrainStateMessage>();

    public IList<TrackStateMessage> Blocks { get; } = new List<TrackStateMessage>();
}
