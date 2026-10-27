namespace TrainControl.Contracts.Messages;

/// <summary>
/// Full set of commands the Train Controller sends to the Train Model each tick.
/// Metric on the wire (power in watts). The emergency brake, when applied, forces
/// zero traction power in the plant; the service brake does not.
/// </summary>
public class TrainCommandMessage
{
    public string TrainId { get; set; } = string.Empty;

    /// <summary>Commanded engine power in watts (plant converts to tractive force).</summary>
    public double PowerCommandWatts { get; set; }

    public bool ServiceBrakeApplied { get; set; }

    public bool EmergencyBrakeApplied { get; set; }

    public bool LeftDoorOpen { get; set; }

    public bool RightDoorOpen { get; set; }

    public bool ExteriorLightsOn { get; set; }

    /// <summary>Target cabin temperature in degrees Celsius (internal SI).</summary>
    public double CabinSetpointCelsius { get; set; }

    public string StationAnnouncement { get; set; } = string.Empty;
}
