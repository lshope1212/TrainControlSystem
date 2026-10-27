using TrainControl.Contracts.Enums;

namespace TrainControl.Contracts.Messages;

/// <summary>Track-derived inputs for a train. A block with no train has an empty TrainId.</summary>
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
}
