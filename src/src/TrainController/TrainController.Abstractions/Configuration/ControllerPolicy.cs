namespace TrainController.Abstractions.Configuration;

/// <summary>
/// Controller behavior values that the project sources have NOT yet defined.
/// </summary>
/// <remarks>
/// PROVISIONAL CONFIGURATION DEFAULTS — NOT PROJECT REQUIREMENTS. Every numeric default
/// below is a placeholder chosen only so the system can run; replace it when the team or
/// customer defines the value. Contrast <see cref="VehicleSpecification"/>, whose values ARE
/// source-backed (FLEXITY 2 datasheet).
/// <para>
/// The same instance is sent to both the Software and the Hardware controller in every
/// <see cref="Inputs.TrainControllerInput"/>, so both implementations always use identical
/// values.
/// </para>
/// </remarks>
public sealed record ControllerPolicy
{
    /// <summary>PROVISIONAL default (0.1 m/s). Speed at or below which the train is treated as stopped.</summary>
    public double StoppedSpeedThresholdMetersPerSecond { get; init; } = 0.1;

    /// <summary>PROVISIONAL default (2.0 m). |distance to station| at or below which the train is treated as at the station.</summary>
    public double StationDistanceThresholdMeters { get; init; } = 2.0;

    /// <summary>
    /// Default 0 m. Extra distance added to the computed station stopping distance. No source
    /// defines a margin, so none is invented.
    /// </summary>
    public double StationBrakingMarginMeters { get; init; }

    /// <summary>
    /// Default 0 m. Extra distance added to the computed stopping distance when protecting the
    /// end of authority. No source defines a margin, so none is invented.
    /// </summary>
    public double AuthorityBrakingMarginMeters { get; init; }

    /// <summary>PROVISIONAL default (30 s). How long doors stay open at a station (both modes).</summary>
    public double StationDwellTimeSeconds { get; init; } = 30.0;

    // ---------------------------------------------------------------- failure detection
    // A track signal loss ALWAYS stops the train with the service brake immediately; these
    // values only control when a condition becomes a LATCHED failure.

    /// <summary>Track signal invalid continuously this long → Signal Pickup Failure latched (0.5 s = 5 ticks at 0.1 s).</summary>
    public double SignalPickupFailureLatchSeconds { get; init; } = 0.5;

    /// <summary>Brake failure is only judged while the speed is above this value (m/s).</summary>
    public double BrakeFailureMinimumSpeedMetersPerSecond { get; init; } = 1.0;

    /// <summary>Service brake must be commanded continuously this long before brake failure is judged (s).</summary>
    public double BrakeFailureDetectionSeconds { get; init; } = 1.0;

    /// <summary>Average deceleration below this over the detection window → Brake Failure (nominal service brake 1.2 m/s²).</summary>
    public double BrakeFailureMinimumDecelerationMetersPerSecondSquared { get; init; } = 0.3;

    /// <summary>Engine failure is only judged while setpoint − actual speed exceeds this value (m/s).</summary>
    public double EngineFailureMinimumSpeedErrorMetersPerSecond { get; init; } = 0.5;

    /// <summary>Engine failure is only judged while commanded power ≥ this fraction of the rated power (10 % of 480 kW = 48 kW).</summary>
    public double EngineFailureMinimumPowerFraction { get; init; } = 0.10;

    /// <summary>Traction conditions must hold continuously this long before engine failure is judged (s).</summary>
    public double EngineFailureDetectionSeconds { get; init; } = 1.0;

    /// <summary>Average acceleration at or below this over the detection window → Engine Failure (m/s²).</summary>
    public double EngineFailureMaximumAccelerationMetersPerSecondSquared { get; init; }

    public static ControllerPolicy Default { get; } = new ControllerPolicy();
}
