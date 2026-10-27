namespace TrainController.Abstractions.Outputs;

/// <summary>Why the power command is what it is this tick (display / diagnostics).</summary>
public enum TractionState
{
    /// <summary>PI is commanding traction power (> 0).</summary>
    Powering = 0,

    /// <summary>No brake applied, but actual speed is at or above the effective target, so power is 0.</summary>
    AtOrAboveTarget,

    /// <summary>Stopped and not ready to depart: service brake held, power 0.</summary>
    HoldingStopped,

    /// <summary>Service brake applied (driver, authority, overspeed, station): power 0.</summary>
    ServiceBrake,

    /// <summary>Doors open / commanded open / open request refused: traction inhibited.</summary>
    DoorInterlock,

    /// <summary>Emergency brake applied: power 0.</summary>
    EmergencyBrake,

    /// <summary>Fail-safe output (communication failure, invalid input/output, fault).</summary>
    FailSafe
}
