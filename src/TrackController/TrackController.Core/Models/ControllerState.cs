using TrainControl.Contracts.Enums;

namespace TrackController.Core.Models;

/// <summary>
/// Placeholder snapshot of what a wayside controller currently commands.
/// </summary>
public class ControllerState
{
    public string WaysideId { get; set; } = string.Empty;

    public SwitchPosition CommandedSwitchPosition { get; set; } = SwitchPosition.Unknown;

    public SignalState CommandedSignalState { get; set; } = SignalState.Unknown;

    public bool CrossingActive { get; set; }
}
