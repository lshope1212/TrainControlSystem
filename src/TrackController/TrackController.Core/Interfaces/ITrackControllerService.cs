using TrackController.Core.Models;

namespace TrackController.Core.Interfaces;

/// <summary>
/// Placeholder internal contract for the wayside controller subsystem.
/// The cross-subsystem contract lives in TrainControl.Contracts.
/// </summary>
public interface ITrackControllerService
{
    ControllerState State { get; }

    ControllerConfiguration Configuration { get; }
}
