using TrainControl.Contracts.Interfaces;
using TrainControl.Contracts.Messages;
using TrainController.Core.Interfaces;
using TrainController.Core.Models;

namespace TrainController.Core.Services;

/// <summary>
/// Stub train controller. Demonstrates that this subsystem exchanges shared contract
/// objects rather than referencing TrainModel.Core directly.
/// No real control equations are implemented yet.
/// </summary>
public class TrainControllerService : ITrainControllerService, ITrainController
{
    public ControllerState State { get; } = new ControllerState();

    public ControllerConfiguration Configuration { get; } = new ControllerConfiguration();

    public void ApplyTrainState(TrainStateMessage state)
    {
        // Placeholder: real controller input handling will live here.
        State.TrainId = state.TrainId;
    }

    public void ApplyAuthority(AuthorityMessage authority)
    {
        // Placeholder: authority enforcement will live here.
        State.AuthorityMeters = authority.AuthorityMeters;
    }

    public SpeedCommandMessage GetSpeedCommand() => new SpeedCommandMessage
    {
        TrainId = State.TrainId,
        CommandedSpeedMetersPerSecond = State.CommandedSpeedMetersPerSecond
    };
}
