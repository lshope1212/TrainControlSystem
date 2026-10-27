namespace TrainControl.Contracts.Messages;

/// <summary>
/// Commands for one train for one controller tick.
/// Direction: Train Controller -> Train Model.
/// </summary>
/// <remarks>
/// All values are SI. Driver-only display data (advisories, alerts, braking guidance)
/// is deliberately NOT part of this contract; it stays inside the Train Controller.
/// </remarks>
public class TrainControllerCommandMessage
{
    public string TrainId { get; set; } = string.Empty;

    /// <summary>Controller tick that produced these commands.</summary>
    public long TickId { get; set; }

    public double PowerCommandWatts { get; set; }

    public bool ServiceBrakeCommand { get; set; }

    public bool EmergencyBrakeCommand { get; set; }

    /// <summary>True = open the left-side doors (side-level command, not per physical door).</summary>
    public bool LeftDoorsOpenCommand { get; set; }

    public bool RightDoorsOpenCommand { get; set; }

    public bool ExteriorLightsCommand { get; set; }

    public double CabinTemperatureSetpointCelsius { get; set; }

    /// <summary>Empty when there is nothing to announce.</summary>
    public string StationAnnouncement { get; set; } = string.Empty;
}
