namespace TrackModel.Core.Models;

/// <summary>Lifecycle of a train the Track Model tracks from Train Model position telemetry.</summary>
public enum TrainTrackingStatus
{
    /// <summary>Dispatch accepted and route registered; waiting for the Train Model's initial position report. Occupies nothing.</summary>
    PendingPlacement,

    /// <summary>On the track: its footprint follows its reported cumulative position.</summary>
    Active,

    /// <summary>
    /// Its telemetry could not be reconciled with the track. The last footprint is held (nothing is cleared)
    /// and no further telemetry is applied until the train is retired.
    /// </summary>
    Faulted,
}

/// <summary>Read-only view of one tracked train (see <see cref="Services.TrackService.TrackedTrains"/>).</summary>
/// <param name="PlannedRoute">The route from the start block (placement route, with any accepted route update).</param>
/// <param name="TraversedPath">Blocks the front has physically entered, in order; the cumulative boundaries are fixed once traversed.</param>
/// <param name="HeadBlockId">Block containing the front; empty until placed.</param>
/// <param name="OccupiedBlockIds">Every block the train physically occupies, rear to front.</param>
public sealed record TrainTrackingSnapshot(
    string TrainId,
    string LineId,
    string DispatchRequestId,
    string StartBlockId,
    IReadOnlyList<string> PlannedRoute,
    IReadOnlyList<string> TraversedPath,
    double FrontPositionMeters,
    double TrainLengthMeters,
    double SpeedMetersPerSecond,
    string HeadBlockId,
    IReadOnlyList<string> OccupiedBlockIds,
    long LastSequence,
    TrainTrackingStatus Status,
    string Fault);

/// <summary>What <see cref="Services.TrackService.RequestTrainPlacement"/> decided.</summary>
public enum TrainPlacementOutcome
{
    /// <summary>Validated and registered: the Train Model should be asked to activate the train.</summary>
    Registered,

    /// <summary>A retry of a placement still waiting for its initial report: ask the Train Model again.</summary>
    AlreadyPending,

    /// <summary>The train is already on the track for this request; nothing changes.</summary>
    AlreadyPlaced,

    /// <summary>Refused; nothing changed.</summary>
    Rejected,
}

public sealed record TrainPlacementResult(TrainPlacementOutcome Outcome, string Detail, bool IsRetryable = false);

/// <summary>What one position report did.</summary>
public enum TrainPositionOutcome
{
    /// <summary>Applied; the footprint may or may not have changed.</summary>
    Applied,

    /// <summary>The initial report: the train now occupies its start block.</summary>
    Placed,

    /// <summary>A repeat of the last applied report; ignored.</summary>
    Duplicate,

    /// <summary>Not applied (stale, malformed, unknown train, ...); the train's state is unchanged.</summary>
    Rejected,

    /// <summary>Not applied, or applied only up to a switch the train could not pass; the train is now faulted and its footprint held.</summary>
    Faulted,

    /// <summary>The initial report arrived but the start block is no longer safe; the placement was withdrawn.</summary>
    PlacementFailed,
}

public sealed record TrainPositionResult(TrainPositionOutcome Outcome, string Detail, string TrainId = "", string DispatchRequestId = "");

/// <summary>A train's footprint changed by one atomic step.</summary>
/// <remarks>
/// Raised synchronously while the layout shows exactly that step, so a listener can capture the block
/// states in order. One telemetry report that crosses several boundaries raises one event per boundary,
/// in the order the train crossed them, so no intermediate occupancy is ever skipped.
/// </remarks>
public sealed class TrackOccupancyTransitionEventArgs(string trainId, string lineId, IReadOnlyList<string> occupied, IReadOnlyList<string> cleared, string headBlockId) : EventArgs
{
    public string TrainId { get; } = trainId;
    public string LineId { get; } = lineId;
    public IReadOnlyList<string> Occupied { get; } = occupied;
    public IReadOnlyList<string> Cleared { get; } = cleared;
    public string HeadBlockId { get; } = headBlockId;
}

/// <summary>Kinds of tracking lifecycle event the host reports onward.</summary>
public enum TrainTrackingEventKind
{
    /// <summary>The initial position report placed the train in its start block.</summary>
    Placed,

    /// <summary>The train faulted; its footprint is held.</summary>
    Faulted,

    /// <summary>The train left the track (retired, layout reloaded, manual removal); its footprint was cleared.</summary>
    Retired,

    /// <summary>A pending placement was dropped before the train reached the track (superseded, undeliverable, start block unsafe).</summary>
    PlacementWithdrawn,
}

public sealed class TrainTrackingEventArgs(TrainTrackingEventKind kind, string trainId, string lineId, string dispatchRequestId, string startBlockId, string detail) : EventArgs
{
    public TrainTrackingEventKind Kind { get; } = kind;
    public string TrainId { get; } = trainId;
    public string LineId { get; } = lineId;
    public string DispatchRequestId { get; } = dispatchRequestId;
    public string StartBlockId { get; } = startBlockId;
    public string Detail { get; } = detail;
}
