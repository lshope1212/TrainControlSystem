namespace TrainControl.Contracts.Messages;

/// <summary>
/// The Train Model's pass-through + status bundle sent to the Train Controller each
/// publish cycle: the track-circuit data the train received and relays (commanded
/// speed/authority, track signal, beacon), plus train-side status the controller
/// needs (passenger emergency brake state). Metric on the wire.
///
/// Actual speed is carried separately by <see cref="TrainStateMessage"/>; this
/// message carries the relayed/authorized values and statuses.
/// </summary>
public class TrainToControllerMessage
{
    public string TrainId { get; set; } = string.Empty;

    // Pass-through from the track circuit
    public double CommandedSpeedMetersPerSecond { get; set; }
    public double CommandedAuthorityMeters { get; set; }

    /// <summary>Track signal usable? "Acquired" or "Lost" (loss = frozen/absent data).</summary>
    public string TrackSignalStatus { get; set; } = "Acquired";

    // Beacon (next-station info the controller uses for stops/announcements)
    public string BeaconStationName { get; set; } = string.Empty;
    public double BeaconDistanceToStopMeters { get; set; }
    public string BeaconPlatformSide { get; set; } = "None";

    /// <summary>Passenger emergency brake state: "Engaged", "Pending", or "Released".</summary>
    public string PassengerEmergencyBrakeStatus { get; set; } = "Released";
}
