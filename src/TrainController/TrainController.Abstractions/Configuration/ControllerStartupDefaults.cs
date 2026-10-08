using TrainController.Abstractions.Inputs;

namespace TrainController.Abstractions.Configuration;

/// <summary>
/// Initial values given to each train's Driver state and Test Model state when the Train
/// Controller process starts.
/// </summary>
/// <remarks>
/// PROVISIONAL CONFIGURATION DEFAULTS — NOT PROJECT REQUIREMENTS. No project source defines
/// any of these values. They exist only so the system can start, and are passed into the
/// state registry so they can be changed in one place (composition root) without code edits
/// elsewhere. Engineer Kp/Ki startup defaults are specified separately in
/// <see cref="EngineerSettings"/> because the project brief does define them.
/// </remarks>
public sealed record ControllerStartupDefaults
{
    /// <summary>PROVISIONAL default. Mode each train's Driver state starts in.</summary>
    public OperatingMode InitialOperatingMode { get; init; } = OperatingMode.Manual;

    /// <summary>PROVISIONAL default (21 °C ≈ 70 °F). Initial driver cabin-temperature request.</summary>
    public double InitialCabinTemperatureSetpointCelsius { get; init; } = 21.0;

    /// <summary>PROVISIONAL default (21 °C ≈ 70 °F). Initial cabin temperature reported by a fresh Test Model state.</summary>
    public double InitialTestCabinTemperatureCelsius { get; init; } = 21.0;

    public static ControllerStartupDefaults Default { get; } = new ControllerStartupDefaults();
}
