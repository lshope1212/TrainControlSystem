using TrainControl.Contracts.Enums;

namespace TrainControl.Contracts.Messages;

/// <summary>Track-derived inputs for a train. A block with no train has an empty TrainId.</summary>
/// <remarks>
/// For an occupied block, speed and authority are the code that applies to the occupying train (zero
/// when the block carries a continuation code for a train entering from another block). A zero is
/// always an authoritative stop code. <see cref="Sequence"/> increases with every Track Model snapshot
/// (seeded from the wall clock at start-up), so a receiver applies only the newest snapshot for each
/// train and a late message can never restore withdrawn authority.
/// </remarks>
public sealed class TrackModelTrainEnvironmentMessage
{
    public Guid SnapshotId { get; set; }
    public string BlockId { get; set; } = string.Empty;
    public string TrainId { get; set; } = string.Empty;
    public double CommandedSpeedMetersPerSecond { get; set; }
    public double ActualSpeedMetersPerSecond { get; set; }
    public double AuthorityMeters { get; set; }
    public SignalState Signal { get; set; }
    public string Beacon { get; set; } = string.Empty;
    public string BeaconTargetBlockId { get; set; } = string.Empty;
    public string NextBlockId { get; set; } = string.Empty;
    public double ElevationMeters { get; set; }
    public double GradePercent { get; set; }
    public double TemperatureCelsius { get; set; }
    public int WaitingPassengers { get; set; }
    public int BoardingPassengers { get; set; }
    public int DisembarkingPassengers { get; set; }
    public int TicketsSold { get; set; }
    public bool HasHeater { get; set; }
    public bool HeaterOn { get; set; }
    public double SpeedLimitMetersPerSecond { get; set; }
    public string TravelDirection { get; set; } = "Bidirectional";

    /// <summary>The wayside movement request the speed/authority belong to; empty when no movement is coded.</summary>
    public string MovementRequestId { get; set; } = string.Empty;

    /// <summary>Monotonic Track Model snapshot number; 0 for an unordered (legacy/test) sender.</summary>
    public long Sequence { get; set; }
}
