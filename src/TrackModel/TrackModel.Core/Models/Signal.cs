using TrainControl.Contracts.Enums;

namespace TrackModel.Core.Models;

/// <summary>
/// Placeholder domain model for a wayside signal.
/// </summary>
public class Signal
{
    public string Id { get; set; } = string.Empty;

    public SignalState State { get; set; } = SignalState.Unknown;
}
