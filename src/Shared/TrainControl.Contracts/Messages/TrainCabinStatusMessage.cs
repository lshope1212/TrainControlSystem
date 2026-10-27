namespace TrainControl.Contracts.Messages;

/// <summary>
/// Actual (sensed) cabin states reported by the Train Model back to the Train
/// Controller: door positions, exterior lights, and measured cabin temperature.
/// Temperature is Celsius on the wire (internal SI).
/// </summary>
public class TrainCabinStatusMessage
{
    public string TrainId { get; set; } = string.Empty;

    public bool LeftDoorOpen { get; set; }

    public bool RightDoorOpen { get; set; }

    public bool ExteriorLightsOn { get; set; }

    public double CabinTemperatureCelsius { get; set; }
}
