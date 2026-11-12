using CTC.Core.Interfaces;
using CTC.Core.Models;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace CTC.Core.Dispatching;

/// <summary>
/// Owns CTC's physical dispatch requests: the only code that sends a
/// <see cref="TrainDispatchRequestMessage"/> or <see cref="TrainRouteUpdateMessage"/>. Both go to the
/// Track Controller, which forwards them to the Track Model; CTC never talks to the Train Model or the
/// Train Controller.
/// </summary>
/// <remarks>
/// <para>
/// A released train that CTC has not yet seen on the track (<see cref="TrainReleaseStatus.AwaitingPlacement"/>)
/// holds one dispatch request for its current plan (line and active route from its start block). Each pass
/// (on every status batch, the 1 s link tick and every time update) sends it if it was never sent, and
/// resends it with the SAME request ID every <see cref="RetryInterval"/> of wall time until the Track Model
/// places the train or refuses it for good: a lost message, a Track Controller or Track Model that was
/// down, an activation the Train Model missed, or a start block that was briefly occupied therefore never
/// loses the dispatch, and the receivers' idempotence means a retry never creates a second train. A
/// changed plan (the dispatcher re-planned before placement) gets a new request ID.
/// </para>
/// <para>
/// The dispatch ends when CTC detects the train in its start block from occupancy, exactly as before; the
/// stages reported along the way (forwarded, activation requested, placed, rejected) are shown to the
/// dispatcher but never move or identify the train. After placement, a dispatcher re-plan that changes the
/// route sends a route update the same way, so the Track Model keeps mapping the train's position along the
/// route it actually runs.
/// </para>
/// </remarks>
public sealed class TrainDispatchCoordinator
{
    /// <summary>Wall time between resends of a request that has not settled.</summary>
    public static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromSeconds(3);

    private readonly IMessageSender _sender;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

    public TrainDispatchCoordinator(IMessageSender sender, TimeProvider? clock = null, TimeSpan? retryInterval = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _clock = clock ?? TimeProvider.System;
        RetryInterval = retryInterval ?? DefaultRetryInterval;
        if (RetryInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryInterval), "The retry interval must be positive.");
        }
    }

    public TimeSpan RetryInterval { get; }

    /// <summary>Raised (on the caller's thread) when a train's dispatch or route update changed, with a dispatcher-readable description.</summary>
    public event Action<DispatchedTrainState, string>? RequestChanged;

    /// <summary>The placement request CTC should hold for <paramref name="train"/> now; null once it is on the track.</summary>
    public static TrainDispatchPlan? PlanPlacement(DispatchedTrainState train)
    {
        ArgumentNullException.ThrowIfNull(train);
        if (train.ReleaseStatus != TrainReleaseStatus.AwaitingPlacement || train.ActiveRouteBlockIds.Count == 0)
        {
            return null;
        }

        int start = Math.Clamp(train.ActiveRouteIndex, 0, train.ActiveRouteBlockIds.Count - 1);
        return new TrainDispatchPlan(train.TrainId, train.LineId, train.ActiveRouteBlockIds.Skip(start).ToList());
    }

    /// <summary>
    /// Sends every dispatch and route update that is due: new ones at once, unsettled ones again after
    /// <see cref="RetryInterval"/>. Send failures are recorded on the request and retried, never thrown.
    /// </summary>
    public async Task SynchronizeAsync(CtcSystemState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var train in state.DispatchedTrains.ToList())
            {
                var request = CurrentRequest(train);
                if (request is not null && IsDue(request))
                {
                    await SendAsync(train, request, cancellationToken);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Records that <paramref name="train"/>, already on the track, now runs <paramref name="routeFromCurrentBlock"/>
    /// (from the block CTC places it in); the route update is sent on the next pass.
    /// </summary>
    public void QueueRouteUpdate(DispatchedTrainState train, IReadOnlyList<string> routeFromCurrentBlock)
    {
        ArgumentNullException.ThrowIfNull(train);
        ArgumentNullException.ThrowIfNull(routeFromCurrentBlock);
        if (train.ReleaseStatus != TrainReleaseStatus.DetectedOnTrack || routeFromCurrentBlock.Count == 0)
        {
            return;
        }

        var plan = new TrainDispatchPlan(train.TrainId, train.LineId, routeFromCurrentBlock.ToList());
        if (train.RouteUpdate is { } pending && pending.Plan.Matches(plan))
        {
            return;
        }

        train.RouteUpdate = new TrainDispatchRequestState(NewRequestId(), plan, isRouteUpdate: true);
    }

    /// <summary>
    /// Records a stage reported for one of CTC's requests. Returns the train it belongs to, or null when it
    /// is stale (not a request CTC still holds) or changes nothing.
    /// </summary>
    public DispatchedTrainState? ApplyStatus(CtcSystemState state, TrainDispatchStatusMessage status)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(status);
        if (string.IsNullOrEmpty(status.RequestId))
        {
            return null;
        }

        foreach (var train in state.DispatchedTrains)
        {
            if (status.Stage is TrainDispatchStage.TrackingFault or TrainDispatchStage.Retired)
            {
                // Lifecycle reports name the placement the train runs under.
                if (train.DispatchRequest?.RequestId == status.RequestId)
                {
                    train.TrackingStatus = $"{status.Stage}: {status.Detail}";
                    return train;
                }

                continue;
            }

            var request = train.DispatchRequest?.RequestId == status.RequestId ? train.DispatchRequest
                : train.RouteUpdate?.RequestId == status.RequestId ? train.RouteUpdate
                : null;
            if (request is null)
            {
                continue;
            }

            // Progress never goes backwards within an attempt (statuses from two modules may cross); a
            // rejection always applies, and progress after a rejection means a retry got further.
            if (IsProgress(status.Stage) && request.Stage is { } current && IsProgress(current) && Rank(status.Stage) < Rank(current))
            {
                return null;
            }

            if (request.Stage == status.Stage && request.StageDetail == status.Detail && request.IsRetryable == status.IsRetryable)
            {
                return null;
            }

            request.Stage = status.Stage;
            request.StageDetail = status.Detail ?? string.Empty;
            request.StageReportedBy = status.ReportedBy ?? string.Empty;
            request.IsRetryable = status.IsRetryable;
            return train;
        }

        return null;
    }

    /// <summary>Dispatcher-readable summary of a train's dispatch or route update.</summary>
    public static string Describe(DispatchedTrainState train)
    {
        ArgumentNullException.ThrowIfNull(train);
        string name = TrainIds.DisplayName(train.TrainId);
        if (train.ReleaseStatus == TrainReleaseStatus.DetectedOnTrack && train.RouteUpdate is { } update)
        {
            return $"{name} route update ({update.Plan.RouteBlockIds.Count} blocks from {update.Plan.StartBlockId}): {DescribeStage(update)}.";
        }

        if (train.DispatchRequest is not { } request)
        {
            return train.ReleaseStatus == TrainReleaseStatus.AwaitingPlacement
                ? $"{name} has no dispatch request yet."
                : $"{name} is on the track.";
        }

        return train.ReleaseStatus == TrainReleaseStatus.DetectedOnTrack
            ? $"{name} detected at {request.Plan.StartBlockId} (inferred from occupancy); dispatch complete."
            : $"{name} dispatch to {request.Plan.StartBlockId}: {DescribeStage(request)}.";
    }

    /// <summary>Short status of one request, e.g. for a grid cell.</summary>
    public static string DescribeStage(TrainDispatchRequestState request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Delivery switch
        {
            MovementRequestDelivery.NotSent => "not sent yet",
            MovementRequestDelivery.Failed when request.Stage is null => $"not delivered ({request.DeliveryError}); retrying",
            _ => request.Stage switch
            {
                null => "delivered to the Track Controller, awaiting its answer",
                TrainDispatchStage.ForwardedToTrackModel => "forwarded to the Track Model",
                TrainDispatchStage.ActivationRequested => "Train Model activation requested; awaiting the train's initial position",
                TrainDispatchStage.Placed => "placed by the Track Model; awaiting start-block occupancy",
                TrainDispatchStage.RouteUpdated => "accepted by the Track Model",
                TrainDispatchStage.Rejected when request.IsRetryable => $"refused by the {request.StageReportedBy} ({request.StageDetail}); retrying",
                TrainDispatchStage.Rejected => $"refused by the {request.StageReportedBy}: {request.StageDetail}",
                _ => $"{request.Stage}: {request.StageDetail}",
            },
        };
    }

    /// <summary>The request the train should hold now (creating a new one when its plan changed); null for none.</summary>
    private static TrainDispatchRequestState? CurrentRequest(DispatchedTrainState train)
    {
        if (train.ReleaseStatus == TrainReleaseStatus.DetectedOnTrack)
        {
            return train.RouteUpdate;
        }

        var plan = PlanPlacement(train);
        if (plan is null)
        {
            return null;
        }

        if (train.DispatchRequest is null || !train.DispatchRequest.Plan.Matches(plan))
        {
            train.DispatchRequest = new TrainDispatchRequestState(NewRequestId(), plan);
        }

        return train.DispatchRequest;
    }

    private bool IsDue(TrainDispatchRequestState request) =>
        !request.IsSettled
        && (request.Delivery == MovementRequestDelivery.NotSent
            || request.LastAttemptAtUtc is not { } attempted
            || _clock.GetUtcNow() - attempted >= RetryInterval);

    private async Task SendAsync(DispatchedTrainState train, TrainDispatchRequestState request, CancellationToken cancellationToken)
    {
        bool initial = request.Delivery == MovementRequestDelivery.NotSent;
        request.LastAttemptAtUtc = _clock.GetUtcNow();
        try
        {
            if (request.IsRouteUpdate)
            {
                await _sender.SendAsync(request.Plan.ToRouteUpdate(request.RequestId), cancellationToken);
            }
            else
            {
                await _sender.SendAsync(request.Plan.ToDispatch(request.RequestId), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            bool newlyFailed = request.Delivery != MovementRequestDelivery.Failed || request.DeliveryError != ex.Message;
            request.Delivery = MovementRequestDelivery.Failed;
            request.DeliveryError = ex.Message;
            if (newlyFailed)
            {
                Notify(train, $"{Describe(train)} The request is kept and resent every {RetryInterval.TotalSeconds:0} s.");
            }

            return;
        }

        request.Delivery = MovementRequestDelivery.Delivered;
        request.DeliveryError = string.Empty;
        request.LastDeliveredAtUtc = _clock.GetUtcNow();
        if (initial)
        {
            // Retries are routine and silent.
            Notify(train, Describe(train));
        }
    }

    private static bool IsProgress(TrainDispatchStage stage) =>
        stage is TrainDispatchStage.ForwardedToTrackModel or TrainDispatchStage.ActivationRequested or TrainDispatchStage.Placed;

    private static int Rank(TrainDispatchStage stage) => stage switch
    {
        TrainDispatchStage.ForwardedToTrackModel => 0,
        TrainDispatchStage.ActivationRequested => 1,
        _ => 2,
    };

    private static string NewRequestId() => Guid.NewGuid().ToString("N");

    private void Notify(DispatchedTrainState train, string message) => RequestChanged?.Invoke(train, message);
}
