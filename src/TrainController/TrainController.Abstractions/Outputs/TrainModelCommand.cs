namespace TrainController.Abstractions.Outputs;

/// <summary>
/// The part of a controller output that goes TO the Train Model (or, in Test Mode, to the
/// Test UI output panel instead). SI units. Mirrors
/// <see cref="TrainControl.Contracts.Messages.TrainControllerCommandMessage"/>.
/// </summary>
public sealed record TrainModelCommand
{
    public double PowerCommandWatts { get; init; }

    public bool ServiceBrakeCommand { get; init; }

    public bool EmergencyBrakeCommand { get; init; }

    public bool LeftDoorsOpenCommand { get; init; }

    public bool RightDoorsOpenCommand { get; init; }

    public bool ExteriorLightsCommand { get; init; }

    public double CabinTemperatureSetpointCelsius { get; init; }

    /// <summary>
    /// Announcement to play on THIS tick only (empty = nothing to announce). Event-like: the
    /// automatic "Next station" (new beacon) and "Arrived" (arrival) announcements and a driver
    /// announcement each appear on exactly one tick.
    /// </summary>
    public string StationAnnouncement { get; init; } = string.Empty;
}
