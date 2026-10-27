using TrainControl.Contracts.Messages;

namespace TrainModel.Core.Models;

/// <summary>
/// The complete set of inputs the Train Model consumes each simulation tick,
/// gathered from the Train Controller, the Track Model, and the local users
/// (Murphy, Passenger). ALL METRIC. This is the plant's "u" vector.
///
/// Defaults are chosen so that an un-wired Train Model (standalone) still runs:
/// no power, no brakes, level track, valid signal.
/// </summary>
public class TrainInputs
{
    // ---- From Train Controller ----
    /// <summary>Commanded engine power, watts.</summary>
    public double PowerCommandWatts { get; set; }

    public bool ServiceBrakeApplied { get; set; }

    public bool EmergencyBrakeApplied { get; set; }

    public bool LeftDoorCommandOpen { get; set; }

    public bool RightDoorCommandOpen { get; set; }

    public bool ExteriorLightCommandOn { get; set; }

    public double CabinSetpointCelsius { get; set; } = 21.0;

    public string StationAnnouncement { get; set; } = string.Empty;

    // ---- From Track Model (control-strategy, via track circuit) ----
    /// <summary>Commanded speed carried by the track circuit, m/s (passed through).</summary>
    public double CommandedSpeedMetersPerSecond { get; set; }

    /// <summary>Commanded authority carried by the track circuit, meters (passed through).</summary>
    public double CommandedAuthorityMeters { get; set; }

    public SignalAvailability TrackSignal { get; set; } = SignalAvailability.Acquired;

    public BeaconMessage? Beacon { get; set; }

    // ---- From Track Model (simulation-only) ----
    /// <summary>Grade of current block, percent. Positive = uphill.</summary>
    public double GradePercent { get; set; }

    /// <summary>Passengers waiting to board at the approaching station.</summary>
    public int PassengerDemand { get; set; }

    // ---- From Murphy (fault injection) ----
    public bool EngineFailure { get; set; }

    public bool BrakeFailure { get; set; }

    public bool SignalPickupFailure { get; set; }

    // ---- From Passenger ----
    /// <summary>Passenger pulled the emergency brake this tick. This only raises a
    /// PENDING request; it does not engage the brake. The Train Controller must
    /// approve it. The passenger cannot clear or approve it.</summary>
    public bool PassengerEmergencyBrakeRequest { get; set; }

    /// <summary>Train Controller APPROVES the pending passenger request (engages the
    /// emergency brake). One-shot command from the controller.</summary>
    public bool ControllerApproveEmergencyBrake { get; set; }

    /// <summary>Train Controller DENIES the pending passenger request (clears pending,
    /// does not engage). One-shot command from the controller.</summary>
    public bool ControllerDenyEmergencyBrake { get; set; }
}

/// <summary>Whether the train currently has a usable track-circuit signal.
/// "Lost" is how signal-pickup failure manifests — there is no positive
/// "I am broken" message, only the absence/freeze of valid data.</summary>
public enum SignalAvailability
{
    Lost = 0,
    Acquired
}
