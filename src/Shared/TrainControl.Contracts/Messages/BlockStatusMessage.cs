using TrainControl.Contracts.Enums;

namespace TrainControl.Contracts.Messages;

/// <summary>
/// Wayside status of a single block.
/// Direction: Track Controller -> CTC.
/// </summary>
/// <remarks>
/// Occupancy does not identify which train occupies the block.
/// </remarks>
public class BlockStatusMessage
{
    public string BlockId { get; set; } = string.Empty;

    public OccupancyState Occupancy { get; set; } = OccupancyState.Unknown;

    public SignalState Signal { get; set; } = SignalState.Unknown;

    public SwitchPosition Switch { get; set; } = SwitchPosition.Unknown;

    public CrossingState Crossing { get; set; } = CrossingState.Unknown;
}
