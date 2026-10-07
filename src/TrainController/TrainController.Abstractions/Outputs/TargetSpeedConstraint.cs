namespace TrainController.Abstractions.Outputs;

/// <summary>
/// Which constraint produced the effective target speed this tick (the binding minimum).
/// Display-only: lets the driver see WHY the target is what it is.
/// </summary>
public enum TargetSpeedConstraint
{
    None = 0,

    /// <summary>Manual mode: the driver's requested speed is the lowest limit.</summary>
    DriverRequest,

    /// <summary>The authorized (wayside) speed is the lowest limit.</summary>
    AuthorizedSpeed,

    /// <summary>The vehicle's nominal maximum speed is the lowest limit.</summary>
    VehicleMaximum,

    /// <summary>The speed from which the train can still stop within the remaining authority.</summary>
    RemainingAuthority,

    /// <summary>Automatic mode: the speed from which the train can still stop at the station / station braking / dwell.</summary>
    StationStop,

    /// <summary>Track signal lost: target 0.</summary>
    TrackSignalLoss,

    /// <summary>Emergency brake applied: target 0.</summary>
    EmergencyBrake,

    /// <summary>
    /// Driver is holding the service brake: target 0 while held. The driver's requested speed
    /// setting is unchanged and becomes the target again when the brake is released.
    /// </summary>
    DriverServiceBrake
}
