using TrainControl.Contracts.Enums;

namespace TrainControl.Contracts.Messages;

/// <summary>
/// Placeholder message describing the state of a single track block.
/// </summary>
public class TrackStateMessage
{
    public string BlockId { get; set; } = string.Empty;

    public bool IsOccupied { get; set; }

    public SignalState Signal { get; set; } = SignalState.Unknown;

    public SwitchPosition Switch { get; set; } = SwitchPosition.Unknown;
}
