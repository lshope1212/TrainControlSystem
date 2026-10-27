using TrainController.Abstractions.Configuration;

namespace TrainController.Abstractions.Inputs;

/// <summary>
/// Driver-originating part of a controller input (always from the Main UI, also in
/// Test Mode). SI units.
/// </summary>
/// <remarks>
/// <see cref="EmergencyBrakeRequested"/> and <see cref="EmergencyBrakeResetRequested"/> are
/// one-shot button presses delivered on exactly one tick: an emergency-brake press LATCHES
/// the emergency brake in the controller; a reset press asks the controller to release the
/// latch, which it only does when no emergency condition remains. The E-brake reset only
/// concerns the driver's and the passengers' emergency brakes. <see cref="ResetFailuresRequested"/>
/// is a separate one-shot press that clears latched failures (signal pickup, brake, engine,
/// Hardware link) whose reset condition is met.
/// </remarks>
public sealed record DriverInput
{
    /// <summary>Record default comes from <see cref="ControllerStartupDefaults"/> (PROVISIONAL).</summary>
    public OperatingMode Mode { get; init; } = ControllerStartupDefaults.Default.InitialOperatingMode;

    /// <summary>
    /// Manual mode only. Null = no driver request: the train follows the commanded speed (still
    /// subject to every other limit). A value can only LOWER the setpoint, never raise it.
    /// </summary>
    public double? RequestedSpeedMetersPerSecond { get; init; }

    /// <summary>Held state: true while the driver keeps the service brake applied.</summary>
    public bool ServiceBrakeRequested { get; init; }

    /// <summary>One-shot press.</summary>
    public bool EmergencyBrakeRequested { get; init; }

    /// <summary>One-shot press. Separate from (and unrelated to) the Test UI simulation Reset.</summary>
    public bool EmergencyBrakeResetRequested { get; init; }

    /// <summary>One-shot press: "Reset failures". Unrelated to the E-brake reset and the Test UI Reset.</summary>
    public bool ResetFailuresRequested { get; init; }

    public bool LeftDoorsOpenRequested { get; init; }

    public bool RightDoorsOpenRequested { get; init; }

    public bool ExteriorLightsRequested { get; init; }

    /// <summary>Record default comes from <see cref="ControllerStartupDefaults"/> (PROVISIONAL).</summary>
    public double CabinTemperatureSetpointCelsius { get; init; } = ControllerStartupDefaults.Default.InitialCabinTemperatureSetpointCelsius;

    /// <summary>
    /// One-shot driver announcement for this tick (empty = none). Sent once as the station
    /// announcement; on that tick it takes precedence over an automatic announcement.
    /// Available in Manual and Automatic mode.
    /// </summary>
    public string AnnouncementRequest { get; init; } = string.Empty;
}
