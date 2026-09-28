using TrackController.Core.Interfaces;
using TrackController.Core.Models;
using TrainControl.Contracts.Interfaces;
using TrainControl.Contracts.Messages;

namespace TrackController.Core.Services;

/// <summary>
/// Stub wayside controller. Exchanges shared contract objects rather than
/// referencing TrackModel.Core directly. No PLC logic yet.
/// </summary>
public class TrackControllerService : ITrackControllerService, ITrackController
{
    public ControllerState State { get; } = new ControllerState();

    public ControllerConfiguration Configuration { get; } = new ControllerConfiguration();

    public void ApplyTrackState(TrackStateMessage state)
    {
        // Placeholder: occupancy / signal input handling will live here.
    }

    public TrackStateMessage GetTrackState(string blockId) => new TrackStateMessage
    {
        BlockId = blockId,
        Signal = State.CommandedSignalState,
        Switch = State.CommandedSwitchPosition
    };
}
