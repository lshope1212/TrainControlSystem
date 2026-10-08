using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;

namespace TrackController.Contracts.Messages;

/// <summary>Reports measured conditions and their observation time; commands are not measurements.</summary>
public sealed class WaysideBlockFeedback
{
    public string BlockId { get; set; } = "";

    public DateTimeOffset ObservedAtUtc { get; set; }

    public OccupancyState Occupancy { get; set; } = OccupancyState.Unknown;

    public SignalState Signal { get; set; } = SignalState.Unknown;

    public SwitchPosition Switch { get; set; } = SwitchPosition.Unknown;

    public CrossingState Crossing { get; set; } = CrossingState.Unknown;

    [JsonPropertyName("faulted")]
    public bool IsFaulted { get; set; }

    [JsonPropertyName("reservedByCtc")]
    public bool? IsReservedByCtc { get; set; }

    public double? ActualSpeedMetersPerSecond { get; set; }
}
