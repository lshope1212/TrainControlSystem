using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;

namespace TrainControl.Contracts.Messages;

/// <summary>Track Model -> Track Controller. Occupancy becomes Unknown for circuit/power failure.</summary>
public sealed record TrackModelBlockStateMessage
{
    /// <summary>Measurement time, preserved through queues and translated for the Pi clock.</summary>
    public DateTimeOffset ObservedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool? IsReservedByCtc { get; set; }
    public double? ActualSpeedMetersPerSecond { get; set; }
    [JsonIgnore]
    public bool IsFaulted => BrokenRail || TrackCircuitFailure || PowerFailure;
    public Guid SnapshotId { get; set; }
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
