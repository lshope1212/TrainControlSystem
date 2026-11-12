using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace CTC.Core.Models;

/// <summary>What CTC asks the track to register for a train: its line and ordered route (the first block is where it starts).</summary>
public sealed record TrainDispatchPlan(string TrainId, string LineId, IReadOnlyList<string> RouteBlockIds)
{
    public string StartBlockId => RouteBlockIds.Count > 0 ? RouteBlockIds[0] : string.Empty;

    /// <summary>Same train, line and route: a retry keeps the request ID.</summary>
    public bool Matches(TrainDispatchPlan other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return TrainId == other.TrainId && LineId == other.LineId && RouteBlockIds.SequenceEqual(other.RouteBlockIds);
    }

    public TrainDispatchRequestMessage ToDispatch(string requestId) => new()
    {
        RequestId = requestId,
        TrainId = TrainId,
        LineId = LineId,
        StartBlockId = StartBlockId,
        RouteBlockIds = [.. RouteBlockIds],
    };

    public TrainRouteUpdateMessage ToRouteUpdate(string requestId) => new()
    {
        RequestId = requestId,
        TrainId = TrainId,
        LineId = LineId,
        RouteBlockIds = [.. RouteBlockIds],
    };
}

/// <summary>
/// One physical dispatch (or route update) CTC sent toward the track, and what it has learned about it,
/// kept apart: transport delivery to the Track Controller, then each downstream stage
/// (<see cref="TrainDispatchStage"/>). None of it says the train is on the track: CTC confirms that only
/// from the start block's anonymous occupancy (<see cref="DispatchedTrainState.ReleaseStatus"/>).
/// </summary>
public sealed class TrainDispatchRequestState
{
    public TrainDispatchRequestState(string requestId, TrainDispatchPlan plan, bool isRouteUpdate = false)
    {
        RequestId = string.IsNullOrWhiteSpace(requestId) ? throw new ArgumentException("A request ID is required.", nameof(requestId)) : requestId;
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        IsRouteUpdate = isRouteUpdate;
    }

    public string RequestId { get; }

    public TrainDispatchPlan Plan { get; }

    /// <summary>A route update for a train already on the track, rather than its placement.</summary>
    public bool IsRouteUpdate { get; }

    public MovementRequestDelivery Delivery { get; set; }

    public string DeliveryError { get; set; } = string.Empty;

    public DateTimeOffset? LastAttemptAtUtc { get; set; }

    public DateTimeOffset? LastDeliveredAtUtc { get; set; }

    /// <summary>The latest stage reported downstream; null until anyone answers.</summary>
    public TrainDispatchStage? Stage { get; set; }

    public string StageDetail { get; set; } = string.Empty;

    /// <summary>"Track Controller" or "Track Model".</summary>
    public string StageReportedBy { get; set; } = string.Empty;

    /// <summary>For a rejection: whether resending the same request can succeed later.</summary>
    public bool IsRetryable { get; set; }

    /// <summary>
    /// Nothing more to send: the Track Model placed the train (or accepted the route update), or refused
    /// it for good. A retryable rejection, a missing answer and every intermediate stage are retried.
    /// </summary>
    public bool IsSettled => Stage is TrainDispatchStage.Placed or TrainDispatchStage.RouteUpdated
        || (Stage == TrainDispatchStage.Rejected && !IsRetryable);
}
