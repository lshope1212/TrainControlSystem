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

    /// <summary>Empty when the block has no station.</summary>
    public string StationName { get; set; } = string.Empty;

    public bool HasSwitch { get; set; }

    public bool HasSignal { get; set; }

    public bool HasCrossing { get; set; }

    /// <summary>IDs of blocks this block connects to.</summary>
    public List<string> ConnectedBlockIds { get; set; } = new List<string>();
}
