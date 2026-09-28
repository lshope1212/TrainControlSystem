using TrainControl.Contracts.Enums;

namespace TrackModel.Core.Models;

/// <summary>
/// Placeholder domain model for a track switch.
/// </summary>
public class TrackSwitch
{
    public string Id { get; set; } = string.Empty;

    public SwitchPosition Position { get; set; } = SwitchPosition.Unknown;
}
