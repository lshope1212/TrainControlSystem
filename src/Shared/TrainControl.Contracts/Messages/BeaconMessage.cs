namespace TrainControl.Contracts.Messages;

/// <summary>
/// Wayside beacon payload delivered to the train at a station approach.
/// Content is the team's to define within the 128-character budget; this
/// typed form carries the fields the Train Model and Train Controller use.
/// Distances are metric (meters) on the wire, per the internal-SI convention.
/// </summary>
public class BeaconMessage
{
    public string TrainId { get; set; } = string.Empty;

    /// <summary>Name of the next station this beacon announces.</summary>
    public string StationName { get; set; } = string.Empty;

    /// <summary>Distance from the beacon to the stopping point, in meters.</summary>
    public double DistanceToStopMeters { get; set; }

    /// <summary>Platform side passengers board/alight on at this station.</summary>
    public PlatformSide Side { get; set; } = PlatformSide.None;

    /// <summary>Optional announcement text to display/play on approach.</summary>
    public string Announcement { get; set; } = string.Empty;
}

/// <summary>Which side of the train the station platform is on.</summary>
public enum PlatformSide
{
    None = 0,
    Left,
    Right
}
