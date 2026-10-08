namespace TrainControl.Contracts.Messages;

/// <summary>
/// Static, non-presentation description of one block within a line.
/// Contains no UI coordinates or styling.
/// </summary>
public class TrackBlockDefinition
{
    public string BlockId { get; set; } = string.Empty;

    public int BlockNumber { get; set; }

    public string Section { get; set; } = string.Empty;

    public double LengthMeters { get; set; }
    public double ElevationMeters { get; set; }
    public double GradePercent { get; set; }
    public double SpeedLimitMetersPerSecond { get; set; }
    public string TravelDirection { get; set; } = "Bidirectional";
    public bool HasHeater { get; set; }
    public string Beacon { get; set; } = string.Empty;
    public string BeaconTargetBlockId { get; set; } = string.Empty;
    public string NormalNextBlockId { get; set; } = string.Empty;
    public string ReverseNextBlockId { get; set; } = string.Empty;

    /// <summary>Civil speed limit of the block, in km/h as given by the track data.</summary>
    public double SpeedLimitKilometersPerHour { get; set; }

    /// <summary>Empty when the block has no station.</summary>
    public string StationName { get; set; } = string.Empty;

    public bool HasSwitch { get; set; }

    public bool HasSignal { get; set; }

    public bool HasCrossing { get; set; }

    /// <summary>IDs of blocks this block connects to.</summary>
    public List<string> ConnectedBlockIds { get; set; } = new List<string>();
}
