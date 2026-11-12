namespace TrainModel.Core.Models;

/// <summary>
/// The physical train being simulated: its fixed specification plus the
/// continuously-evolving kinematic and onboard state. Pure domain model —
/// no WPF, no I/O. ALL UNITS METRIC (internal SI).
/// </summary>
public class Train
{
    public string Id { get; set; } = "TRAIN-001";

    /// <summary>Line the train is assigned to (e.g. "Red Line"). Display/roster only.</summary>
    public string LineName { get; set; } = "Red Line";

    /// <summary>High-level operating state for the roster (Operating / Holding / Yard).</summary>
    public TrainOperatingStatus OperatingStatus { get; set; } = TrainOperatingStatus.Operating;

    /// <summary>Fixed physical specification (mass, limits, capacity, dimensions).</summary>
    public TrainSpecs Specs { get; set; } = new();

    // ---- Kinematics (the plant's state vector) ----
    /// <summary>Cumulative distance the FRONT of the train has travelled since it was activated
    /// (dispatched), meters: 0 at activation, integrated from velocity. Not a block, not a map
    /// coordinate, never reset at a block boundary; the Track Model maps it onto the route.</summary>
    public double PositionMeters { get; set; }

    /// <summary>Current speed, m/s. This is the headline output: actual speed.</summary>
    public double VelocityMetersPerSecond { get; set; }

    /// <summary>Current acceleration, m/s^2 (signed; negative = decelerating).</summary>
    public double AccelerationMetersPerSecondSquared { get; set; }

    /// <summary>Previous-tick acceleration, retained for trapezoidal integration.</summary>
    public double PreviousAccelerationMetersPerSecondSquared { get; set; }

    // ---- Onboard / cabin state ----
    public PassengerState Passengers { get; set; } = new();

    // --- Automatic station-exchange arm state ---
    /// <summary>Absolute position (m) of the stop point the current beacon armed, or null if unarmed.</summary>
    public double? StationStopTargetMeters { get; set; }
    /// <summary>True once an exchange has fired for the currently-armed stop (prevents re-firing while
    /// the train sits at the platform). Re-armed when a new beacon sets a new target.</summary>
    public bool HasExchangedAtArmedStop { get; set; }

    public bool LeftDoorOpen { get; set; }

    public bool RightDoorOpen { get; set; }

    public bool ExteriorLightsOn { get; set; }

    /// <summary>Actual cabin temperature, degrees Celsius.</summary>
    public double CabinTemperatureCelsius { get; set; } = 21.0;

    // ---- Fault state (latched by Murphy; emergency brake latched until controller clears) ----
    public bool EngineFailed { get; set; }

    public bool BrakeFailed { get; set; }

    public bool SignalPickupFailed { get; set; }

    /// <summary>A passenger has pulled the emergency brake and the request is awaiting
    /// the Train Controller's approval. Pending alone does NOT engage the brake.</summary>
    public bool PassengerEmergencyBrakePending { get; set; }

    /// <summary>Emergency brake currently engaged. Engaged ONLY by an approved
    /// Train Controller command — never by a passenger request on its own.</summary>
    public bool EmergencyBrakeEngaged { get; set; }

    /// <summary>Total current mass, kg: empty consist + passengers + crew.</summary>
    public double TotalMassKg =>
        Specs.EmptyMassKg
        + (Passengers.PassengerCount + Specs.CrewCount) * TrainSpecs.PassengerMassKg;
}


/// <summary>Roster-level operating state of a train.</summary>
public enum TrainOperatingStatus
{
    Operating = 0,
    Holding,
    Yard
}
