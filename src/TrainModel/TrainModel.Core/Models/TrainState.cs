namespace TrainModel.Core.Models;

/// <summary>
/// Immutable snapshot of the Train Model's full observable state at the end of a
/// tick: the plant's outputs plus pass-through values and statuses. ALL METRIC —
/// the UI converts to imperial for display. This is what the ViewModels bind to
/// and what the output messages are built from.
/// </summary>
public class TrainState
{
    public string TrainId { get; set; } = string.Empty;

    // ---- Headline plant outputs ----
    public double PositionMeters { get; set; }

    public double VelocityMetersPerSecond { get; set; }

    public double AccelerationMetersPerSecondSquared { get; set; }

    // ---- Occupancy (derived from position + length) ----
    public double FrontPositionMeters { get; set; }

    public double TrainLengthMeters { get; set; }

    /// <summary>Line assignment (roster display).</summary>
    public string LineName { get; set; } = string.Empty;

    /// <summary>Roster-level operating status.</summary>
    public TrainOperatingStatus OperatingStatus { get; set; } = TrainOperatingStatus.Operating;

    // ---- Pass-through from the track circuit to the controller ----
    public double CommandedSpeedMetersPerSecond { get; set; }

    public double CommandedAuthorityMeters { get; set; }

    public SignalAvailability TrackSignal { get; set; } = SignalAvailability.Acquired;

    // ---- Beacon / station info (from Track Model, passed through) ----
    public string BeaconStationName { get; set; } = string.Empty;
    public double BeaconDistanceToStopMeters { get; set; }
    public string BeaconPlatformSide { get; set; } = "None";
    public string StationAnnouncement { get; set; } = string.Empty;

    // ---- Terrain (from Track Model, simulation) ----
    public double GradePercent { get; set; }

    // ---- Brake / door / cabin actual states ----
    public bool ServiceBrakeEngaged { get; set; }

    public bool EmergencyBrakeEngaged { get; set; }

    public bool LeftDoorOpen { get; set; }

    public bool RightDoorOpen { get; set; }

    public bool ExteriorLightsOn { get; set; }

    public double CabinTemperatureCelsius { get; set; }

    // ---- Fault status ----
    public bool EngineFailed { get; set; }

    public bool BrakeFailed { get; set; }

    public bool SignalPickupFailed { get; set; }

    public bool PassengerEmergencyBrakeActive { get; set; }

    /// <summary>A passenger request is awaiting Train Controller approval (not yet engaged).</summary>
    public bool PassengerEmergencyBrakePending { get; set; }

    // ---- Passengers ----
    public int PassengerCount { get; set; }

    public int LastBoarded { get; set; }

    public int LastAlighted { get; set; }

    /// <summary>Passengers left on the platform because the train was full.</summary>
    public int LastLeftBehind { get; set; }

    /// <summary>Whether the last exchange hit the capacity ceiling.</summary>
    public bool WasAtCapacity { get; set; }
}
