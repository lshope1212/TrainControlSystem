using TrainController.Core.Models;

namespace TrainController.Core.Interfaces;

/// <summary>
/// Placeholder internal contract for the train controller subsystem.
/// The cross-subsystem contract lives in TrainControl.Contracts.
/// </summary>
public interface ITrainControllerService
{
    ControllerState State { get; }

    ControllerConfiguration Configuration { get; }
}
