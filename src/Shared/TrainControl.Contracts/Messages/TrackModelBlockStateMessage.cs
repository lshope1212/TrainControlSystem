using TrainControl.Contracts.Enums;

namespace TrainControl.Contracts.Messages;

/// <summary>Track Model -> Track Controller. Occupancy becomes Unknown for circuit/power failure.</summary>
public sealed class TrackModelBlockStateMessage
{
    public string BlockId { get; set; } = string.Empty;
    public OccupancyState Occupancy { get; set; }
    public bool BrokenRail { get; set; }
    public bool TrackCircuitFailure { get; set; }
    public bool PowerFailure { get; set; }
    public bool IsClosed { get; set; }
    public SwitchPosition Switch { get; set; }
    public SignalState Signal { get; set; }
    public CrossingState Crossing { get; set; }
}
