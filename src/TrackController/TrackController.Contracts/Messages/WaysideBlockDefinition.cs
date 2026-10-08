using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;

namespace TrackController.Contracts.Messages;

/// <summary>Describes track geometry and protection zones independently of controller ownership.</summary>
public sealed class WaysideBlockDefinition
{
    public string BlockId { get; set; } = "";

    public double LengthMeters { get; set; }

    public double SpeedLimitMetersPerSecond { get; set; }

    public List<string> NextBlockIds { get; set; } = [];

    public bool HasSignal { get; set; }

    public string? NormalNextBlockId { get; set; }

    public string? ReverseNextBlockId { get; set; }

    public string? StemBlockId { get; set; }

    public List<string> SwitchProtectionBlockIds { get; set; } = [];

    public bool HasCrossing { get; set; }

    public List<string> CrossingProtectionBlockIds { get; set; } = [];

    public double MapX { get; set; }

    public double MapY { get; set; }

    /// <summary>Optional platform name shown on the territory map, such as Station B.</summary>
    public string? StationName { get; set; }
}
