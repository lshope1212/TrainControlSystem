using TrainControl.Contracts.Messages;

namespace TrainControl.Contracts.Interfaces;

/// <summary>
/// Placeholder cross-subsystem contract for a wayside track controller.
/// </summary>
public interface ITrackController
{
    void ApplyTrackState(TrackStateMessage state);

    TrackStateMessage GetTrackState(string blockId);
}
