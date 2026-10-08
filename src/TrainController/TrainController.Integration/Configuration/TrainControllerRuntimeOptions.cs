using TrainController.Abstractions.Configuration;

namespace TrainController.Integration.Configuration;

/// <summary>
/// Runtime configuration of the Train Controller subsystem, built once in the composition root.
/// </summary>
public sealed record TrainControllerRuntimeOptions
{
    /// <summary>
    /// PROVISIONAL default (0.1 s) — not a project requirement. Fixed SIMULATED timestep of
    /// every controller tick. The speed multiplier never changes it; controllers integrate
    /// with it, never with wall-clock time.
    /// </summary>
    public double SimulationTimeStepSeconds { get; init; } = 0.1;

    /// <summary>Speed multipliers offered by the Test UI. 1x = one tick per timestep of wall-clock time.</summary>
    public IReadOnlyList<int> SupportedSpeedMultipliers { get; init; } = new[] { 1, 2, 5, 10 };

    public VehicleSpecification Vehicle { get; init; } = VehicleSpecification.Flexity2Blackpool;

    public ControllerPolicy Policy { get; init; } = ControllerPolicy.Default;

    public ControllerStartupDefaults StartupDefaults { get; init; } = ControllerStartupDefaults.Default;

    public static TrainControllerRuntimeOptions Default { get; } = new TrainControllerRuntimeOptions();

    /// <exception cref="ArgumentException">An option is unusable.</exception>
    public void Validate()
    {
        if (!double.IsFinite(SimulationTimeStepSeconds) || SimulationTimeStepSeconds <= 0.0)
        {
            throw new ArgumentException("SimulationTimeStepSeconds must be finite and > 0.");
        }

        if (SupportedSpeedMultipliers is null || SupportedSpeedMultipliers.Count == 0 || SupportedSpeedMultipliers.Any(m => m <= 0))
        {
            throw new ArgumentException("SupportedSpeedMultipliers must be a non-empty list of positive values.");
        }

        if (Vehicle is null || Policy is null || StartupDefaults is null)
        {
            throw new ArgumentException("Vehicle, Policy and StartupDefaults are required.");
        }
    }
}
