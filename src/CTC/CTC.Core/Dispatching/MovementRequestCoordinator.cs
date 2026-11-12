using CTC.Core.Interfaces;
using CTC.Core.Models;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace CTC.Core.Dispatching;

/// <summary>
/// Owns CTC's movement requests at the Track Controller: the only code that sends, renews or
/// cancels a <see cref="MovementSuggestionMessage"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each pass reconciles what every train SHOULD hold (<see cref="MovementRequestPlanner"/>) with
/// what CTC issued (<see cref="DispatchedTrainState.MovementRequest"/>), under one async lock, so
/// rapid status or clock messages and dispatcher actions can never interleave two updates for a
/// train. The result is idempotent: repeating a pass changes nothing.
/// </para>
/// <list type="bullet">
///   <item>Same plan: a true renewal. The same request ID and the same target, path, speed and
///   authority are resent once <see cref="RenewalInterval"/> of wall time has passed, so the Track
///   Controller's wall-clock request watchdog (10 s) keeps a still-valid request alive. A renewal
///   never adds distance. A request the Track Controller rejected is not renewed.</item>
///   <item>Block-to-block handoff (prepare, transfer, retire): while the Track Controller grants the
///   active request with authority reaching into the next block, a successor for that block (new
///   request ID, entry = the current block, the same fixed end point) is prepared behind it
///   (<see cref="DispatchedTrainState.StagedMovementRequest"/>). The Track Controller only stages it
///   (<see cref="RequestAcknowledgementOutcome.PreparedAsSuccessor"/>, never a grant) and, when it
///   observes the train entering that block, promotes it and retires the predecessor in one step.
///   When CTC then tracks the train into the block and the successor is exactly the plan it needs,
///   CTC adopts it as the active request: no cancel-then-request gap. The predecessor is cancelled
///   afterwards only as an idempotent clean-up.</item>
///   <item>Different plan (the route was re-planned, a block ahead became unsafe, or no valid
///   successor was prepared): the old request (and any prepared successor) is cancelled first,
///   then a new request ID is issued, so no conflicting old request stays active.</item>
///   <item>No plan (awaiting placement, completed, no safe authority): the old request and any
///   prepared successor are cancelled.</item>
/// </list>
/// <para>
/// Renewal runs on wall time because the watchdog does: it does not speed up with the simulation
/// and continues while the simulation is paused. Pausing therefore neither expires a valid request
/// nor extends one, since no renewal ever changes a request's limits. If CTC stops (or loses its
/// pipe), renewals stop and the Track Controller's watchdog withdraws the authorization; prepared
/// successors expire under the same watchdog.
/// </para>
/// <para>
/// A send failure is recorded on the request and retried with the same request ID, so a retry can
/// never create a duplicate movement. Periodic passes retry a failed delivery at most once per
/// <see cref="RenewalInterval"/>, so an unreachable Track Controller cannot tie up every pass in
/// connection timeouts; a dispatcher action retries at once.
/// </para>
/// </remarks>
public sealed class MovementRequestCoordinator
{
    /// <summary>Wall time between renewals: well inside the Track Controller's 10 s request watchdog.</summary>
    public static readonly TimeSpan DefaultRenewalInterval = TimeSpan.FromSeconds(3);

    private readonly IMessageSender _sender;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

    public MovementRequestCoordinator(IMessageSender sender, TimeProvider? clock = null, TimeSpan? renewalInterval = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _clock = clock ?? TimeProvider.System;
        RenewalInterval = renewalInterval ?? DefaultRenewalInterval;
        if (RenewalInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(renewalInterval), "The renewal interval must be positive.");
        }
    }

    public TimeSpan RenewalInterval { get; }

    /// <summary>Raised (on the caller's thread) when a train's request changed, with a dispatcher-readable description.</summary>
    public event Action<DispatchedTrainState, string>? RequestChanged;

    /// <summary>
    /// Brings every dispatched train's request in line with its plan and renews the ones that are
    /// due. Send failures are recorded on the train and retried on the next pass, never thrown.
    /// </summary>
    public async Task SynchronizeAsync(CtcSystemState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var train in state.DispatchedTrains.ToList())
            {
                var line = state.FindLine(train.LineId);
                var desired = line is null ? null : MovementRequestPlanner.Plan(line, train);
                try
                {
                    await ApplyAsync(train, line, desired, renewIfDue: true, rollBackOnFailure: false, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Recorded on the request; the next pass retries.
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Replaces <paramref name="train"/>'s request with <paramref name="desired"/> (cancelling the old
    /// one, and any prepared successor, first) for a plan the caller commits only if this succeeds.
    /// </summary>
    /// <exception cref="Exceptions.MessageSendException">A send failed. The train then holds no new request.</exception>
    public async Task ReplaceAsync(DispatchedTrainState train, MovementRequestPlan? desired, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(train);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // A dispatcher re-plan sets a new end point: it never adopts a successor prepared for the old one.
            await ApplyAsync(train, line: null, desired, renewIfDue: false, rollBackOnFailure: true, cancellationToken, allowHandoff: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Records the Track Controller's acknowledgement of a movement request (active or prepared
    /// successor). Returns the train it belongs to, or null when it is stale (not a request CTC still
    /// holds) or a repeat.
    /// </summary>
    public DispatchedTrainState? ApplyAcknowledgement(CtcSystemState state, RequestAcknowledgementMessage acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(acknowledgement);

        var (train, request) = FindByRequestId(state, acknowledgement.RequestId);
        bool retired = acknowledgement.Outcome == RequestAcknowledgementOutcome.Rejected && acknowledgement.IsRetiredMovement;
        if (request is null || request.IsRetired
            || (!retired && request.Acknowledgement == acknowledgement.Outcome && request.AcknowledgementDetail == acknowledgement.Detail))
        {
            return null;
        }

        request.Acknowledgement = acknowledgement.Outcome;
        request.IsRetired = retired;
        request.AcknowledgementDetail = acknowledgement.Detail ?? string.Empty;
        if (acknowledgement.Outcome == RequestAcknowledgementOutcome.Rejected)
        {
            // A rejected request authorizes nothing, whatever an earlier report said.
            SetAuthorization(request, MovementAuthorizationState.None, 0, 0, string.Empty);
        }

        return train;
    }

    /// <summary>
    /// Records the Track Controller's authorization for the request targeting <paramref name="blockId"/>.
    /// A live report without a decision means the wayside holds no request there. Returns the
    /// affected train, or null when nothing changed or the report is for a request CTC does not hold.
    /// </summary>
    /// <remarks>
    /// A report may name a prepared successor CTC has not adopted yet (the Track Controller promoted
    /// it at the train's transition, and the report arrived before CTC moved the train): it is
    /// recorded on that successor, which CTC adopts on its next pass.
    /// </remarks>
    public DispatchedTrainState? ApplyAuthorization(CtcSystemState state, string blockId, MovementAuthorizationReport? report, bool isLiveReport)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (report is null)
        {
            if (!isLiveReport)
            {
                return null;
            }

            var holder = state.DispatchedTrains.FirstOrDefault(train => train.MovementRequest is { } held
                && held.Plan.TargetBlockId == blockId
                && held.Authorization != MovementAuthorizationState.None);
            if (holder?.MovementRequest is not { } lost)
            {
                return null;
            }

            SetAuthorization(lost, MovementAuthorizationState.None, 0, 0, "The Track Controller reports no request for this block.");
            return holder;
        }

        var (train, request) = FindByRequestId(state, report.RequestId);
        if (request is null || request.IsRetired || request.Plan.TargetBlockId != blockId)
        {
            return null;
        }

        bool changed = request.Authorization != report.State
            || Math.Abs(request.AuthorizedSpeedMetersPerSecond - report.AuthorizedSpeedMetersPerSecond) > 1e-9
            || Math.Abs(request.AuthorizedAuthorityMeters - report.AuthorizedAuthorityMeters) > 1e-9
            || request.AuthorizationReason != (report.Reason ?? string.Empty)
            || request.SupersededRequestId != (report.SupersededRequestId ?? string.Empty);

        // The wayside evaluating it means it accepted it, even if that acknowledgement was lost.
        request.Acknowledgement ??= RequestAcknowledgementOutcome.AcceptedForEvaluation;
        if (!changed)
        {
            return null;
        }

        request.SupersededRequestId = report.SupersededRequestId ?? string.Empty;
        SetAuthorization(request, report.State, report.AuthorizedSpeedMetersPerSecond, report.AuthorizedAuthorityMeters, report.Reason ?? string.Empty);
        return train;
    }

    /// <summary>Dispatcher-readable summary of a train's request, e.g. for status text.</summary>
    public static string Describe(DispatchedTrainState train)
    {
        ArgumentNullException.ThrowIfNull(train);

        string name = TrainIds.DisplayName(train.TrainId);
        if (train.MovementRequest is not { } request)
        {
            return train.ReleaseStatus == TrainReleaseStatus.AwaitingPlacement
                ? $"{name} holds no movement request: it is awaiting placement on the track."
                : $"{name} holds no movement request.";
        }

        string route = request.Plan.PathBlockIds.Count == 0
            ? $"block {request.Plan.TargetBlockId}"
            : $"blocks {request.Plan.TargetBlockId} to {request.Plan.PathBlockIds[^1]}";
        string state = request.IsRetired ? "ended at the Track Controller, waiting for current block authority" : request.Delivery switch
        {
            MovementRequestDelivery.Failed => $"not delivered ({request.DeliveryError})",
            MovementRequestDelivery.NotSent => "not sent yet",
            _ => request.Acknowledgement switch
            {
                null => "delivered, awaiting Track Controller acknowledgement",
                RequestAcknowledgementOutcome.Rejected => $"rejected by the Track Controller: {request.AcknowledgementDetail}",
                _ => request.Authorization switch
                {
                    MovementAuthorizationState.Approved => "authorized by the Track Controller",
                    MovementAuthorizationState.Restricted => $"authorized with restrictions: {request.AuthorizationReason}",
                    MovementAuthorizationState.Withheld => $"accepted, but authority withheld: {request.AuthorizationReason}",
                    _ => request.Acknowledgement == RequestAcknowledgementOutcome.PreparedAsSuccessor ? "prepared as a successor" : "accepted for evaluation",
                },
            },
        };
        string handoff = request.SupersededRequestId.Length > 0 && request.IsMovementAuthorized
            ? " Authority was handed over from the previous block's request."
            : string.Empty;
        string successor = train.StagedMovementRequest is { } staged
            ? staged.IsRetired ? $" The request for block {staged.Plan.TargetBlockId} ended." : staged.Acknowledgement switch
            {
                RequestAcknowledgementOutcome.Rejected => $" The successor for block {staged.Plan.TargetBlockId} was rejected: {staged.AcknowledgementDetail}",
                RequestAcknowledgementOutcome.PreparedAsSuccessor => $" Successor for block {staged.Plan.TargetBlockId} prepared (not a grant; it takes over when the train enters that block).",
                _ => $" Successor for block {staged.Plan.TargetBlockId} sent, awaiting preparation.",
            }
            : string.Empty;
        return $"{name} movement request for {route} ({request.Plan.AuthorityMeters:0} m): {state}.{handoff}{successor}";
    }

    private async Task ApplyAsync(DispatchedTrainState train, CtcLineState? line, MovementRequestPlan? desired, bool renewIfDue, bool rollBackOnFailure, CancellationToken cancellationToken, bool allowHandoff = true)
    {
        await RetryCleanupAsync(train, cancellationToken);

        var current = train.MovementRequest;
        var staged = train.StagedMovementRequest;

        // Adopt only a successor the Track Controller reports taking over. If it was still staged,
        // cancelling the predecessor can retire its ID. The ordinary replacement below then needs
        // a fresh ID so the wayside can evaluate it from the newly occupied block.
        if (allowHandoff && current is not null && desired is not null && staged is not null && !current.Plan.Matches(desired)
            && staged.Plan.Matches(desired) && staged.SupersededRequestId == current.RequestId
            && staged.Acknowledgement != RequestAcknowledgementOutcome.Rejected)
        {
            train.MovementRequest = staged;
            train.StagedMovementRequest = null;
            staged.PredecessorRequestId = null; // renewed from now on as an ordinary request
            QueueCleanup(train, current);
            Notify(train, $"{TrainIds.DisplayName(train.TrainId)}'s movement authority passed from block {current.Plan.TargetBlockId} to block {staged.Plan.TargetBlockId} under the prepared request.");
            await RetryCleanupAsync(train, cancellationToken);
            current = staged;
            staged = null;
        }

        if (current is not null && desired is not null && current.Plan.Matches(desired))
        {
            if (current.Acknowledgement == RequestAcknowledgementOutcome.Rejected)
            {
                // The wayside may already have promoted the successor. Keep it until CTC observes
                // the new block, then adopt it through the normal handoff path above.
                if (!current.IsRetired)
                    await WithdrawSuccessorAsync(train, cancellationToken);
                return;
            }

            if (IsDue(current, renewIfDue))
            {
                await SendAsync(train, current, cancellationToken);
            }

            if (line is not null && !current.IsRetired)
            {
                await SynchronizeSuccessorAsync(train, line, current, renewIfDue, cancellationToken);
            }

            return;
        }

        if (current is not null)
        {
            if (current.HasBeenAttempted)
            {
                // Throws on failure: the old request stays recorded and is cancelled on a later pass,
                // and the replacement is not sent while the old one may still be active.
                await _sender.SendAsync(current.Plan.ToCancellation(current.RequestId), cancellationToken);
            }

            train.MovementRequest = null;
            await WithdrawSuccessorAsync(train, cancellationToken);
            if (desired is null)
            {
                Notify(train, $"{TrainIds.DisplayName(train.TrainId)}'s movement request for block {current.Plan.TargetBlockId} was cancelled.");
                return;
            }
        }
        else
        {
            await WithdrawSuccessorAsync(train, cancellationToken);
        }

        if (desired is null)
        {
            return;
        }

        var request = new MovementRequestState(Guid.NewGuid().ToString("N"), desired);
        train.MovementRequest = request;
        try
        {
            await SendAsync(train, request, cancellationToken);
        }
        catch (Exception ex) when (rollBackOnFailure && ex is not OperationCanceledException)
        {
            train.MovementRequest = null;
            throw;
        }
    }

    /// <summary>
    /// Keeps the prepared successor in line with the active request: prepares it once the Track
    /// Controller grants authority reaching into the next block, renews it like any request, replaces
    /// it when the plan for the next block changes, and withdraws it when no valid successor exists
    /// (the grant was withheld or no longer reaches the next block).
    /// </summary>
    private async Task SynchronizeSuccessorAsync(DispatchedTrainState train, CtcLineState line, MovementRequestState current, bool renewIfDue, CancellationToken cancellationToken)
    {
        var plan = MovementRequestPlanner.PlanSuccessor(line, train, current);
        var staged = train.StagedMovementRequest;
        if (staged is not null && (plan is null || !staged.Plan.Matches(plan) || staged.PredecessorRequestId != current.RequestId))
        {
            await WithdrawSuccessorAsync(train, cancellationToken);
            staged = null;
        }

        if (plan is null)
        {
            return;
        }

        if (staged is null)
        {
            staged = new MovementRequestState(Guid.NewGuid().ToString("N"), plan, current.RequestId);
            train.StagedMovementRequest = staged;
            await SendAsync(train, staged, cancellationToken);
            return;
        }

        if (staged.Acknowledgement != RequestAcknowledgementOutcome.Rejected && IsDue(staged, renewIfDue))
        {
            await SendAsync(train, staged, cancellationToken);
        }
    }

    /// <summary>Drops the prepared successor; one the Track Controller may hold is cancelled (idempotent).</summary>
    private async Task WithdrawSuccessorAsync(DispatchedTrainState train, CancellationToken cancellationToken)
    {
        if (train.StagedMovementRequest is not { } staged)
        {
            return;
        }

        train.StagedMovementRequest = null;
        QueueCleanup(train, staged);
        await RetryCleanupAsync(train, cancellationToken);
    }

    private static void QueueCleanup(DispatchedTrainState train, MovementRequestState retired)
    {
        if (retired.HasBeenAttempted && !train.UncancelledRequests.Any(request => request.RequestId == retired.RequestId))
        {
            train.UncancelledRequests.Add(retired);
        }
    }

    /// <summary>Sends pending clean-up cancellations. Never throws for a send failure: they are retried on the next pass.</summary>
    private async Task RetryCleanupAsync(DispatchedTrainState train, CancellationToken cancellationToken)
    {
        foreach (var retired in train.UncancelledRequests.ToList())
        {
            try
            {
                await _sender.SendAsync(retired.Plan.ToCancellation(retired.RequestId), cancellationToken);
                train.UncancelledRequests.Remove(retired);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The Track Controller's watchdog withdraws an unrenewed request anyway; retry later.
                return;
            }
        }
    }

    private bool IsDue(MovementRequestState request, bool renewIfDue) => request.Delivery switch
    {
        MovementRequestDelivery.Delivered => renewIfDue && IsAtLeastRenewalIntervalAgo(request.LastDeliveredAtUtc),
        MovementRequestDelivery.Failed => !renewIfDue || IsAtLeastRenewalIntervalAgo(request.LastAttemptAtUtc),
        _ => true,
    };

    private async Task SendAsync(DispatchedTrainState train, MovementRequestState request, CancellationToken cancellationToken)
    {
        bool initial = request.Delivery != MovementRequestDelivery.Delivered;
        request.HasBeenAttempted = true;
        request.LastAttemptAtUtc = _clock.GetUtcNow();
        try
        {
            await _sender.SendAsync(request.Plan.ToMessage(request.RequestId, request.PredecessorRequestId), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            bool newlyFailed = request.Delivery != MovementRequestDelivery.Failed || request.DeliveryError != ex.Message;
            request.Delivery = MovementRequestDelivery.Failed;
            request.DeliveryError = ex.Message;
            if (newlyFailed)
            {
                Notify(train, Describe(train));
            }

            throw;
        }

        request.Delivery = MovementRequestDelivery.Delivered;
        request.DeliveryError = string.Empty;
        request.LastDeliveredAtUtc = _clock.GetUtcNow();
        if (initial)
        {
            // Renewals are routine and silent.
            Notify(train, Describe(train));
        }
    }

    private bool IsAtLeastRenewalIntervalAgo(DateTimeOffset? time) => time is not { } at || _clock.GetUtcNow() - at >= RenewalInterval;

    private static void SetAuthorization(MovementRequestState request, MovementAuthorizationState state, double speed, double authority, string reason)
    {
        request.Authorization = state;
        request.AuthorizedSpeedMetersPerSecond = speed;
        request.AuthorizedAuthorityMeters = authority;
        request.AuthorizationReason = reason;
    }

    /// <summary>The train and request (active or prepared successor) with <paramref name="requestId"/>.</summary>
    private static (DispatchedTrainState? Train, MovementRequestState? Request) FindByRequestId(CtcSystemState state, string? requestId)
    {
        if (string.IsNullOrEmpty(requestId))
        {
            return (null, null);
        }

        foreach (var train in state.DispatchedTrains)
        {
            if (train.MovementRequest?.RequestId == requestId)
            {
                return (train, train.MovementRequest);
            }

            if (train.StagedMovementRequest?.RequestId == requestId)
            {
                return (train, train.StagedMovementRequest);
            }
        }

        return (null, null);
    }

    private void Notify(DispatchedTrainState train, string message) => RequestChanged?.Invoke(train, message);
}
