namespace TrainController.Abstractions.Inputs;

/// <summary>
/// Train-Model-originating part of a controller input, in SI units. Supplied by the real
/// Train Model in Normal Mode, or by the Test UI's per-train test state in Test Mode —
/// never by both.
/// </summary>
/// <remarks>
/// Track grade is intentionally absent: it belongs to Train Model physics; the controller
/// only reacts to the resulting <see cref="ActualSpeedMetersPerSecond"/>.
/// </remarks>
public sealed record TrainModelInput
{
    /// <summary>
    /// Train is dispatched and in service. The Train Controller never owns this value: in
    /// Normal Mode it comes from the Train Model / system state; in Test Mode it comes from
    /// that train's Test Model state (the Test UI standing in for the Train Model).
    /// Controller ticks execute only for active trains.
    /// </summary>
    public bool IsActive { get; init; }

    public double ActualSpeedMetersPerSecond { get; init; }

    public double AuthorizedSpeedMetersPerSecond { get; init; }

    /// <summary>Latest authority value received from the track (via the Train Model).</summary>
    public double RemainingAuthorityMeters { get; init; }

    /// <summary>
    /// True on the tick a NEW authority value is received. The controller then recalibrates its
    /// remaining-authority estimate to <see cref="RemainingAuthorityMeters"/>; between updates it
    /// decreases the estimate by ActualSpeed × DeltaTime. Set by the model-input provider (Test UI:
    /// when the tester sends an authority; Normal Mode: the Train Model adapter).
    /// </summary>
    public bool AuthorityUpdateReceived { get; init; }

    /// <summary>Defaults to false: an unset input must not look like a healthy track signal.</summary>
    public bool TrackSignalValid { get; init; }

    public BeaconData Beacon { get; init; } = BeaconData.None;

    public bool PassengerEmergencyBrakeRequested { get; init; }

    public bool LeftDoorsOpen { get; init; }

    public bool RightDoorsOpen { get; init; }

    public bool ExteriorLightsOn { get; init; }

    public double CabinTemperatureCelsius { get; init; }
}
