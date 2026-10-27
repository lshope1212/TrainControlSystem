using TrainController.Abstractions.Configuration;

namespace TrainController.Abstractions.Inputs;

/// <summary>
/// Everything one controller evaluation needs for one train and one tick:
/// identity/timing + Train Model input + Driver input + Engineer settings + vehicle data
/// + policy values. Identical for the Software and the Hardware controller.
/// </summary>
/// <remarks>
/// All units SI. <see cref="DeltaTimeSeconds"/> is the fixed SIMULATED timestep; the
/// simulation speed multiplier changes how often ticks run, never this value, and
/// controllers must integrate with it rather than with wall-clock time.
/// </remarks>
public sealed record TrainControllerInput
{
    public string TrainId { get; init; } = string.Empty;

    /// <summary>Monotonic tick counter; echoed by the output so responses can be matched to requests.</summary>
    public long TickId { get; init; }

    public double SimulationTimeSeconds { get; init; }

    public double DeltaTimeSeconds { get; init; }

    public TrainModelInput Model { get; init; } = new TrainModelInput();

    public DriverInput Driver { get; init; } = new DriverInput();

    public EngineerSettings Engineer { get; init; } = EngineerSettings.Default;

    public VehicleSpecification Vehicle { get; init; } = VehicleSpecification.Flexity2Blackpool;

    public ControllerPolicy Policy { get; init; } = ControllerPolicy.Default;
}
