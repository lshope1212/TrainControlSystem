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

    /// <summary>
    /// PROVISIONAL default (<see cref="TrackSignalLossResponse.ServiceBrakeStop"/>). Brake used when
    /// the track signal is lost. The requirement only mandates braking, not a specific brake.
    /// </summary>
    public TrackSignalLossResponse TrackSignalLossResponse { get; init; } = TrackSignalLossResponse.ServiceBrakeStop;

    /// <summary>PROVISIONAL default (30 s). How long doors stay open at a station in Automatic mode.</summary>
    public double StationDwellTimeSeconds { get; init; } = 30.0;

    public static ControllerPolicy Default { get; } = new ControllerPolicy();
}
