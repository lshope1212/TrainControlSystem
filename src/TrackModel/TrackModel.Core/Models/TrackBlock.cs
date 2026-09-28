namespace TrackModel.Core.Models;

/// <summary>
/// Pure domain model for a single section of track. Placeholder properties only.
/// </summary>
public class TrackBlock
{
    public string Id { get; set; } = string.Empty;

    public double LengthMeters { get; set; }

    public double GradePercent { get; set; }

    public double SpeedLimitMetersPerSecond { get; set; }

    public bool IsOccupied { get; set; }

    public bool IsClosed { get; set; }
}
