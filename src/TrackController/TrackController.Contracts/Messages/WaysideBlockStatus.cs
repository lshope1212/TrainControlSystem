using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Contracts.Messages;
/// <summary>Combines block conditions with separate commanded and reported equipment states.</summary>
public sealed record WaysideBlockStatus(
    string BlockId,
    double LengthMeters,
    double SpeedLimitMetersPerSecond,
    IReadOnlyList<string> NextBlockIds,
    double MapX,
    double MapY,
    bool HasSwitch,
    bool HasSignal,
    bool HasCrossing,
    OccupancyState Occupancy,
    string Maintenance,
    [property: JsonPropertyName("fresh")] bool IsFresh,
    [property: JsonPropertyName("faulted")] bool IsFaulted,
    [property: JsonPropertyName("reservedByCtc")] bool? IsReservedByCtc,
    SwitchPosition CommandedSwitch,
    SwitchPosition ReportedSwitch,
    SignalState CommandedSignal,
    SignalState ReportedSignal,
    CrossingState CommandedCrossing,
    CrossingState ReportedCrossing,
    double? ActualSpeedMetersPerSecond,
    string Status,
    string? StationName = null)
{
    public MovementSuggestionMessage? Request { get; init; }
    public double AuthorizedSpeedMetersPerSecond { get; init; }
    public double AuthorizedAuthorityMeters { get; init; }
    public string MovementStatus { get; init; } = "";
    public string MovementReason { get; init; } = "";
}
