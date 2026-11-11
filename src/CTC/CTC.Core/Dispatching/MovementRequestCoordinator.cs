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
///   <item>Different plan (the train entered a new block, the route was re-planned, a block ahead
///   became unsafe): the old request is cancelled first, then a new request ID is issued, so no
///   conflicting old request stays active.</item>
///   <item>No plan (awaiting placement, completed, no safe authority): the old request is cancelled.</item>
/// </list>
/// <para>
/// Renewal runs on wall time because the watchdog does: it does not speed up with the simulation
/// and continues while the simulation is paused. Pausing therefore neither expires a valid request
/// nor extends one, since no renewal ever changes a request's limits. If CTC stops (or loses its
/// pipe), renewals stop and the Track Controller's watchdog withdraws the authorization.
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
                    await ApplyAsync(train, desired, renewIfDue: true, rollBackOnFailure: false, cancellationToken);
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
    /// one first) for a plan the caller commits only if this succeeds.
    /// </summary>
    /// <exception cref="Exceptions.MessageSendException">A send failed. The train then holds no new request.</exception>
    public async Task ReplaceAsync(DispatchedTrainState train, MovementRequestPlan? desired, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(train);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await ApplyAsync(train, desired, renewIfDue: false, rollBackOnFailure: true, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Records the Track Controller's acknowledgement of a movement request. Returns the train it
    /// belongs to, or null when it is stale (not a request CTC still holds) or a repeat.
    /// </summary>
    public DispatchedTrainState? ApplyAcknowledgement(CtcSystemState state, RequestAcknowledgementMessage acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(acknowledgement);

        var train = FindByRequestId(state, acknowledgement.RequestId);
        var request = train?.MovementRequest;
        if (request is null || (request.Acknowledgement == acknowledgement.Outcome && request.AcknowledgementDetail == acknowledgement.Detail))
        {
            return null;
        }

        request.Acknowledgement = acknowledgement.Outcome;
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

        var train = FindByRequestId(state, report.RequestId);
        if (train?.MovementRequest is not { } request || request.Plan.TargetBlockId != blockId)
        {
            return null;
        }

        bool changed = request.Authorization != report.State
            || Math.Abs(request.AuthorizedSpeedMetersPerSecond - report.AuthorizedSpeedMetersPerSecond) > 1e-9
            || Math.Abs(request.AuthorizedAuthorityMeters - report.AuthorizedAuthorityMeters) > 1e-9
            || request.AuthorizationReason != (report.Reason ?? string.Empty);

        // The wayside evaluating it means it accepted it, even if that acknowledgement was lost.
        request.Acknowledgement ??= RequestAcknowledgementOutcome.AcceptedForEvaluation;
        if (!changed)
        {
            return null;
        }

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
        string state = request.Delivery switch
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
                    _ => "accepted for evaluation",
                },
            },
        };
        return $"{name} movement request for {route} ({request.Plan.AuthorityMeters:0} m): {state}.";
    }

    private async Task ApplyAsync(DispatchedTrainState train, MovementRequestPlan? desired, bool renewIfDue, bool rollBackOnFailure, CancellationToken cancellationToken)
    {
        var current = train.MovementRequest;
        if (current is not null && desired is not null && current.Plan.Matches(desired))
        {
            if (current.Acknowledgement == RequestAcknowledgementOutcome.Rejected)
            {
                return;
            }

            bool due = current.Delivery switch
            {
                MovementRequestDelivery.Delivered => renewIfDue && IsAtLeastRenewalIntervalAgo(current.LastDeliveredAtUtc),
                MovementRequestDelivery.Failed => !renewIfDue || IsAtLeastRenewalIntervalAgo(current.LastAttemptAtUtc),
                _ => true,
            };
            if (due)
            {
                await SendAsync(train, current, cancellationToken);
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
            if (desired is null)
            {
                Notify(train, $"{TrainIds.DisplayName(train.TrainId)}'s movement request for block {current.Plan.TargetBlockId} was cancelled.");
                return;
            }
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

    private async Task SendAsync(DispatchedTrainState train, MovementRequestState request, CancellationToken cancellationToken)
    {
        bool initial = request.Delivery != MovementRequestDelivery.Delivered;
        request.HasBeenAttempted = true;
        request.LastAttemptAtUtc = _clock.GetUtcNow();
        try
        {
            await _sender.SendAsync(request.Plan.ToMessage(request.RequestId), cancellationToken);
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

    private static DispatchedTrainState? FindByRequestId(CtcSystemState state, string? requestId) =>
        string.IsNullOrEmpty(requestId) ? null : state.DispatchedTrains.FirstOrDefault(train => train.MovementRequest?.RequestId == requestId);

    private void Notify(DispatchedTrainState train, string message) => RequestChanged?.Invoke(train, message);
}
