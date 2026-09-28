using TrainControl.Contracts.Messages;

namespace TrainControl.Contracts.Interfaces;

/// <summary>
/// Placeholder cross-subsystem contract for a train controller.
/// Implemented by TrainController.Core, consumed by other subsystems
/// without taking a direct project reference on it.
/// </summary>
public interface ITrainController
{
    void ApplyTrainState(TrainStateMessage state);

    void ApplyAuthority(AuthorityMessage authority);

    SpeedCommandMessage GetSpeedCommand();
}
