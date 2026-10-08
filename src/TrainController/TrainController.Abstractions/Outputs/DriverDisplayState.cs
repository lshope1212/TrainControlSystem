using TrainControl.Contracts.Enums;

namespace TrainController.Abstractions.Outputs;

/// <summary>
/// Driver-facing information produced by the controller for the Main UI. Never sent to
/// the Train Model. SI units; the UI converts to imperial for display.
/// </summary>
public sealed record DriverDisplayState
{
    /// <summary>
    /// Final internal speed target after applying ALL constraints (driver request in Manual,
    /// authorized speed, vehicle maximum, remaining authority, station stop in Automatic,
    /// signal loss, emergency brake). Never simply the driver's request.
    /// </summary>
    public double EffectiveTargetSpeedMetersPerSecond { get; init; }

    /// <summary>The constraint that determined <see cref="EffectiveTargetSpeedMetersPerSecond"/>.</summary>
    public TargetSpeedConstraint TargetLimitedBy { get; init; }

    public string NextStationName { get; init; } = string.Empty;

    /// <summary>
    /// Station-relative estimate (beacon-calibrated, dead-reckoned between beacons). Signed: may be
    /// slightly negative just past the station. Null when no station target exists (no valid beacon
    /// yet, or the station was departed / passed).
    /// </summary>
    public double? DistanceToNextStationMeters { get; init; }

    public PlatformSide PlatformSide { get; init; } = PlatformSide.None;

    /// <summary>
    /// Controller's remaining-authority estimate: recalibrated on each authority update and
    /// decreased by v·dt between updates (clamped at 0).
    /// </summary>
    public double RemainingAuthorityMeters { get; init; }

    /// <summary>A new authority value recalibrated the estimate this tick.</summary>
    public bool AuthorityRecalibrated { get; init; }

    /// <summary>v² / (2·service decel) at the current speed, plus the configured station margin.</summary>
    public double ServiceBrakeStoppingDistanceMeters { get; init; }

    /// <summary>
    /// Distance from the train to the latest point at which service braking must begin to
    /// stop at the station. ≤ 0 means braking is due now. Null when no station is known.
    /// </summary>
    public double? DistanceToStationBrakePointMeters { get; init; }

    /// <summary>
    /// Distance from the train to the point where authority protection applies the service brake
    /// (remaining authority − service stopping distance − authority margin − v·dt; stopped:
    /// remaining authority − margin). ≤ 0 means authority braking is active. Null only when the
    /// sender does not provide it (e.g. an older Hardware build).
    /// </summary>
    public double? DistanceToAuthorityBrakePointMeters { get; init; }

    /// <summary>Manual mode: station braking is due (advisory to the driver).</summary>
    public bool StationBrakingAdvised { get; init; }

    /// <summary>Automatic mode: controller is applying station braking itself.</summary>
    public bool StationBrakingActive { get; init; }

    public bool AtStation { get; init; }

    public StationEvent StationEvent { get; init; }

    /// <summary>Why the power command is what it is.</summary>
    public TractionState TractionState { get; init; }

    public bool AuthorityProtectionActive { get; init; }

    public bool OverspeedProtectionActive { get; init; }

    /// <summary>Traction inhibited because doors are open, or a door request was refused while moving.</summary>
    public bool DoorInterlockActive { get; init; }

    public EmergencyBrakeCause EmergencyBrakeCauses { get; init; }

    /// <summary>True when the latch is set and a driver reset is required.</summary>
    public bool EmergencyBrakeLatched { get; init; }

    /// <summary>
    /// Empty when no reset is needed or a reset would be accepted now; otherwise why a driver
    /// reset is currently blocked (e.g. passenger request still active).
    /// </summary>
    public string EmergencyBrakeResetBlockedReason { get; init; } = string.Empty;

    /// <summary>A driver emergency-brake reset was requested this tick and refused (an emergency condition remains).</summary>
    public bool EmergencyBrakeResetRejected { get; init; }

    /// <summary>A newly received valid beacon recalibrated the station distance this tick.</summary>
    public bool BeaconRecalibrated { get; init; }

    /// <summary>Track-circuit speed/authority signal is lost or invalid; the controller is stopping the train.</summary>
    public bool TrackSignalLost { get; init; }

    public bool ControllerFaulted { get; init; }

    public string FaultReason { get; init; } = string.Empty;

    public IReadOnlyList<string> Alerts { get; init; } = Array.Empty<string>();
}
