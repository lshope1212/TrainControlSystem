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

    /// <summary>
    /// For a switch some live CTC route needs: what that route needs and whether the controller has
    /// commanded it, is waiting for the Track Model's report, or cannot move it (and why). Empty when
    /// no route needs the switch. Explanation only; it never changes a command.
    /// </summary>
    public string SwitchAlignment { get; init; } = "";

    /// <summary>
    /// Set on the next block of a granted movement whose successor is staged: that movement's own
    /// authority expressed from this block's entry, for a train entering from
    /// <see cref="WaysideContinuation.EntryBlockId"/>. It is not a second grant (see
    /// <see cref="WaysideContinuation"/>). Null otherwise.
    /// </summary>
    public WaysideContinuation? Continuation { get; init; }
}

/// <summary>
/// A prepared authority handoff: the granted predecessor's authority, measured from the next block's
/// entry (the same fixed end point, never further), coded onto that block so the train keeps its
/// authority when it crosses the boundary. It belongs to <see cref="PredecessorRequestId"/>; it
/// disappears the moment that grant is withheld, and is replaced by the successor's own evaluated
/// grant once the Track Controller observes the transition and promotes
/// <see cref="SuccessorRequestId"/>.
/// </summary>
public sealed record WaysideContinuation(
    string PredecessorRequestId,
    string SuccessorRequestId,
    string EntryBlockId,
    double AuthorizedSpeedMetersPerSecond,
    double AuthorizedAuthorityMeters);
