using CTC.Core.Dispatching;
using CTC.Core.Exceptions;
using CTC.Core.Interfaces;
using CTC.Core.Models;
using CTC.Core.Routing;
using CTC.Core.Scheduling;
using CTC.Core.Tracking;
using TrainControl.Common.Validation;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace CTC.Core.Services;

/// <summary>
/// CTC office service. Maps shared contract messages into <see cref="CtcSystemState"/>
/// and builds outbound contract messages. Transmission is delegated to an injected
/// <see cref="IMessageSender"/>; this class knows nothing about the transport.
/// Dispatching is time-triggered only (see <see cref="SetSystemTimeAsync"/>). Speed and
/// authority rules live in <see cref="SpeedPlanner"/> and <see cref="AuthorityManager"/>,
/// routing in <see cref="RouteManager"/>, train location inference in
/// <see cref="TrainLocationTracker"/>, movement-plan timing (stops, dwell) in
/// <see cref="MovementPlanner"/>, the movement requests held at the Track Controller in
/// <see cref="MovementRequestCoordinator"/>, and physical dispatch in <see cref="TrainDispatchCoordinator"/>.
/// </summary>
/// <remarks>
/// <para>
/// Responsibilities are split deliberately. CTC decides WHEN a train is released and proposes its
/// route, speed and authority. The Track Controller validates every movement against occupancy,
/// equipment, maintenance and territory and is the final authority; CTC reports its acknowledgement
/// and actual authorization separately from transport delivery. At release CTC sends a physical
/// dispatch (<see cref="TrainDispatchRequestMessage"/>) to the Track Controller, which forwards it to the
/// Track Model; the Track Model places the train and has the Train Model activate it. CTC never talks to
/// the Train Model or the Train Controller. A released train stays
/// <see cref="TrainReleaseStatus.AwaitingPlacement"/>, with its dispatch retried until it settles, until
/// its start block reports occupied: delivery or acceptance of the dispatch is never reported as a
/// physical release.
/// </para>
/// <para>
/// A movement's speed and fixed authority end are decided when the movement starts: at release,
/// when a train departs a stop, and when the dispatcher re-plans a released train. Moving from
/// block to block re-expresses the same authority end from the new block (with a new request ID)
/// but never extends it.
/// </para>
/// <para>
/// Async methods resume on the caller's synchronization context, so when called from the
/// UI thread every state change also happens on the UI thread.
/// </para>
/// </remarks>
public class CTCService : ICTCService
{
    /// <summary>
    /// Wall time after which a live wayside status that was not refreshed is treated as stale
    /// (Unknown). The Track Controller reports every block about twice a second.
    /// </summary>
    public static readonly TimeSpan BlockStatusStaleAfter = TimeSpan.FromSeconds(3);

    private readonly IMessageSender _messageSender;
    private readonly TimeProvider _clock;
    private readonly MovementRequestCoordinator _movements;
    private readonly TrainDispatchCoordinator _dispatches;

    // Trains whose next movement is being started right now (see AdvanceMovementPlansAsync).
    private readonly HashSet<string> _movementsInFlight = new HashSet<string>();

    public CTCService(IMessageSender messageSender)
        : this(messageSender, null)
    {
    }

    /// <param name="clock">Wall clock for status freshness and request renewal; the system clock by default.</param>
    /// <param name="requestRenewalInterval">Wall time between movement request renewals; see <see cref="MovementRequestCoordinator.DefaultRenewalInterval"/>.</param>
    public CTCService(IMessageSender messageSender, TimeProvider? clock, TimeSpan? requestRenewalInterval = null)
    {
        _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
        _clock = clock ?? TimeProvider.System;
        _movements = new MovementRequestCoordinator(messageSender, _clock, requestRenewalInterval);
        _movements.RequestChanged += (train, message) =>
            OnStateChanged(CtcStateChangeKind.MovementRequestUpdated, train.MovementRequest?.Plan.TargetBlockId ?? train.CurrentBlockId, train.TrainId, message);
        _dispatches = new TrainDispatchCoordinator(messageSender, _clock, requestRenewalInterval);
        _dispatches.RequestChanged += (train, message) =>
            OnStateChanged(CtcStateChangeKind.TrainDispatchRequestUpdated, train.DispatchRequest?.Plan.StartBlockId ?? train.CurrentBlockId, train.TrainId, message);
    }

    public CtcSystemState State { get; } = new CtcSystemState();

    /// <inheritdoc />
    public void ResetOperations()
    {
        State.DispatchedTrains.Clear();
        State.DispatchQueue.Clear();
        State.ScheduledTrains.Clear();
        _movementsInFlight.Clear();
        foreach (var line in State.Lines)
        {
            line.TicketSalesPerHour = 0;
            foreach (var block in line.Blocks)
            {
                block.PendingMaintenanceRequestId = string.Empty;
                block.PendingSwitchRequestId = string.Empty;
                block.RequestedMaintenanceState = block.ConfirmedMaintenanceState;
            }
        }

        OnStateChanged(CtcStateChangeKind.Schedule, message: "System reset: schedule cleared.");
        OnStateChanged(CtcStateChangeKind.DispatchQueue, message: "System reset: dispatch queue cleared.");
        OnStateChanged(CtcStateChangeKind.TicketSales);
        OnStateChanged(CtcStateChangeKind.BlockStatus, message: "System reset: all trains removed.");
    }

    public event EventHandler<CtcStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Brings CTC's territory in line with the layout, line by line (see
    /// <see cref="TrackLayoutReconciler"/>): an equivalent repeated layout changes nothing, and an
    /// incompatible change to a line with released trains is refused for that line.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A line or block ID is blank or duplicated (block IDs must be unique across all lines); nothing changed.
    /// </exception>
    public TrackLayoutSyncResult ApplyTrackLayout(TrackLayoutMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var result = TrackLayoutReconciler.Apply(State, message);

        if (result.Changed)
        {
            OnStateChanged(CtcStateChangeKind.TrackLayout, message: result.Summary);
        }

        if (result.DroppedScheduledTrainIds.Count > 0)
        {
            OnStateChanged(CtcStateChangeKind.Schedule, message: result.Summary);
            OnStateChanged(CtcStateChangeKind.DispatchQueue);
        }

        if (result.RejectedChanges.Count > 0)
        {
            OnStateChanged(CtcStateChangeKind.TrackLayoutRejected, message: string.Join(" ", result.RejectedChanges));
        }

        return result;
    }

    /// <summary>
    /// Records the Track Controller's latest report for a block, then lets
    /// <see cref="TrainLocationTracker"/> infer whether a dispatched train moved into it.
    /// The report never identifies a train; occupancy that no single train explains stays anonymous.
    /// </summary>
    /// <remarks>
    /// A live report (one with <see cref="BlockStatusMessage.ReportedAtUtc"/>) older than the one
    /// already applied for the block is a late duplicate and is ignored entirely. Repeated identical
    /// reports change nothing and raise no event. A report's movement decision is applied to the
    /// request it names; decisions for any other request ID are ignored.
    /// </remarks>
    public void ApplyBlockStatus(BlockStatusMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        CtcBlockState block = GetBlock(message.BlockId);

        bool isLive = message.ReportedAtUtc is not null;
        if (isLive && block.StatusReportedAtUtc is { } applied && message.ReportedAtUtc < applied)
        {
            return;
        }

        // Captured first: only a change INTO Occupied can move a train, never a repeated snapshot. A block
        // whose last definite report was Occupied and that only went Unknown in between (stale reports,
        // a track circuit fault) shows no new entry: a long train still straddling it is not a new train.
        OccupancyState previousOccupancy = block.LastDefiniteOccupancy == OccupancyState.Occupied ? OccupancyState.Occupied : block.Occupancy;
        bool changed = block.IsStatusStale
            || block.Occupancy != message.Occupancy
            || block.Signal != message.Signal
            || block.Switch != message.Switch
            || block.Crossing != message.Crossing
            || block.ConfirmedMaintenanceState != message.Maintenance;

        block.Occupancy = message.Occupancy;
        if (message.Occupancy is OccupancyState.Clear or OccupancyState.Occupied)
        {
            block.LastDefiniteOccupancy = message.Occupancy;
        }

        block.Signal = message.Signal;
        block.Switch = message.Switch;
        block.Crossing = message.Crossing;

        // Only the confirmed state: RequestedMaintenanceState records what CTC asked for
        // and is never overwritten by a wayside report.
        block.ConfirmedMaintenanceState = message.Maintenance;

        block.StatusReportedAtUtc = message.ReportedAtUtc;
        block.LiveStatusReceivedAtUtc = isLive ? _clock.GetUtcNow() : null;
        block.IsStatusStale = false;

        if (changed)
        {
            OnStateChanged(CtcStateChangeKind.BlockStatus, block.BlockId);
        }

        var location = TrainLocationTracker.ApplyOccupancy(State, block, previousOccupancy);
        if (location.DetectedTrain is { } detected)
        {
            OnStateChanged(
                CtcStateChangeKind.TrainDetectedOnTrack,
                block.BlockId,
                detected.TrainId,
                $"{TrainIds.DisplayName(detected.TrainId)} detected at its start block {block.BlockId} (inferred from occupancy{DispatchStageNote(detected)}). Its movement requests start now.");
        }
        else if (location.AdvancedTrain is { } advanced)
        {
            OnStateChanged(CtcStateChangeKind.TrainLocationUpdated, block.BlockId, advanced.TrainId);

            // Entering the current stop starts its dwell; the dwell ends on a later SystemTime update.
            if (MovementPlanner.ApplyTrainAdvanced(advanced, State.SystemTime))
            {
                OnStateChanged(CtcStateChangeKind.TrainMovementPhaseChanged, block.BlockId, advanced.TrainId, DescribePhase(advanced));
            }
        }
        else if (location.IsAmbiguous)
        {
            string trainNames = string.Join(" or ", location.AmbiguousTrains.Select(train => TrainIds.DisplayName(train.TrainId)));
            OnStateChanged(
                CtcStateChangeKind.TrainLocationAmbiguous,
                block.BlockId,
                message: $"Block {block.BlockId} became occupied, but it could be {trainNames}; no train location was changed.");
        }

        if (_movements.ApplyAuthorization(State, block.BlockId, message.Movement, isLive) is { } authorized)
        {
            OnStateChanged(CtcStateChangeKind.MovementRequestUpdated, block.BlockId, authorized.TrainId, MovementRequestCoordinator.Describe(authorized));
        }
    }

    /// <summary>
    /// Records the Track Controller's answer to a CTC request. Answers to requests CTC no longer
    /// waits on (stale, repeated, or with no request ID) are ignored.
    /// </summary>
    public void ApplyRequestAcknowledgement(RequestAcknowledgementMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        switch (message.RequestType)
        {
            case nameof(MovementSuggestionMessage):
                if (_movements.ApplyAcknowledgement(State, message) is { } train)
                {
                    OnStateChanged(CtcStateChangeKind.MovementRequestUpdated, message.BlockId, train.TrainId, MovementRequestCoordinator.Describe(train));
                }

                break;

            case nameof(MaintenanceRequestMessage):
                if (FindPendingBlock(message, block => block.PendingMaintenanceRequestId) is { } maintenanceBlock)
                {
                    maintenanceBlock.PendingMaintenanceRequestId = string.Empty;
                    string requested = maintenanceBlock.RequestedMaintenanceState == MaintenanceState.Closed ? "close" : "reopen";
                    if (message.Outcome == RequestAcknowledgementOutcome.Rejected)
                    {
                        // The request is dead: stop showing it as pending so the dispatcher can act again.
                        maintenanceBlock.RequestedMaintenanceState = maintenanceBlock.ConfirmedMaintenanceState;
                        OnStateChanged(CtcStateChangeKind.MaintenanceRequest, maintenanceBlock.BlockId);
                        OnStateChanged(CtcStateChangeKind.RequestAcknowledged, maintenanceBlock.BlockId, message: $"Track Controller rejected the request to {requested} Block {maintenanceBlock.BlockId}: {message.Detail}");
                    }
                    else
                    {
                        OnStateChanged(CtcStateChangeKind.RequestAcknowledged, maintenanceBlock.BlockId, message: $"Track Controller accepted the request to {requested} Block {maintenanceBlock.BlockId} ({message.Detail}). The block's state changes when the Track Controller confirms it.");
                    }
                }

                break;

            case nameof(SwitchPositionRequestMessage):
                if (FindPendingBlock(message, block => block.PendingSwitchRequestId) is { } switchBlock)
                {
                    switchBlock.PendingSwitchRequestId = string.Empty;
                    OnStateChanged(
                        CtcStateChangeKind.RequestAcknowledged,
                        switchBlock.BlockId,
                        message: message.Outcome == RequestAcknowledgementOutcome.Rejected
                            ? $"Track Controller rejected the switch request for Block {switchBlock.BlockId}: {message.Detail}"
                            : $"Track Controller accepted the switch request for Block {switchBlock.BlockId} ({message.Detail}). The displayed position changes only when the switch reports it.");
                }

                break;
        }
    }

    /// <summary>
    /// Records a stage of a physical dispatch or route update (forwarded, activation requested, placed,
    /// rejected, ...) reported by the Track Controller or, through it, the Track Model. Stages of requests
    /// CTC no longer holds are ignored. Never moves or identifies a train: CTC still confirms placement
    /// from the start block's occupancy.
    /// </summary>
    public void ApplyTrainDispatchStatus(TrainDispatchStatusMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (_dispatches.ApplyStatus(State, message) is { } train)
        {
            string detail = message.Stage is TrainDispatchStage.TrackingFault or TrainDispatchStage.Retired
                ? $"{TrainIds.DisplayName(train.TrainId)}: the Track Model reports {message.Stage}: {message.Detail}"
                : TrainDispatchCoordinator.Describe(train);
            OnStateChanged(CtcStateChangeKind.TrainDispatchRequestUpdated, message.StartBlockId, train.TrainId, detail);
        }
    }

    /// <summary>What CTC knew of the train's dispatch when it detected the train, for the detection message.</summary>
    private static string DispatchStageNote(DispatchedTrainState train) => train.DispatchRequest?.Stage switch
    {
        TrainDispatchStage.Placed => "; the Track Model reported the placement",
        TrainDispatchStage.ActivationRequested => "; Train Model activation was requested",
        null when train.DispatchRequest is null => "; no dispatch was sent",
        _ => string.Empty,
    };

    /// <summary>
    /// Marks every block whose live status was not refreshed within <see cref="BlockStatusStaleAfter"/>
    /// as stale: occupancy and equipment become Unknown (so the block is unsafe for authority) until
    /// a new report arrives. A status from a non-periodic sender never goes stale. Returns how many
    /// blocks went stale now.
    /// </summary>
    public int RefreshBlockStatusFreshness()
    {
        var now = _clock.GetUtcNow();
        var stale = new List<CtcBlockState>();
        foreach (var block in State.Lines.SelectMany(line => line.Blocks))
        {
            if (!block.IsStatusStale && block.LiveStatusReceivedAtUtc is { } received && now - received > BlockStatusStaleAfter)
            {
                block.IsStatusStale = true;
                block.Occupancy = OccupancyState.Unknown;
                block.Signal = SignalState.Unknown;
                block.Switch = SwitchPosition.Unknown;
                block.Crossing = CrossingState.Unknown;
                stale.Add(block);
                OnStateChanged(CtcStateChangeKind.BlockStatus, block.BlockId);
            }
        }

        if (stale.Count > 0)
        {
            string blocks = stale.Count <= 5 ? string.Join(", ", stale.Select(block => block.BlockId)) : $"{stale.Count} blocks";
            OnStateChanged(
                CtcStateChangeKind.BlockStatusStale,
                message: $"No Track Controller report for {blocks} in {BlockStatusStaleAfter.TotalSeconds:0} s; their status is Unknown until reports resume.");
        }

        return stale.Count;
    }

    /// <summary>
    /// Brings every dispatched train's movement request at the Track Controller in line with its
    /// plan and current location, and renews still-valid requests that are due (see
    /// <see cref="MovementRequestCoordinator"/>). Failures are reported and retried, never thrown.
    /// </summary>
    public async Task SynchronizeMovementRequestsAsync(CancellationToken cancellationToken = default)
    {
        await _dispatches.SynchronizeAsync(State, cancellationToken);
        await ContinueIntoNextSegmentsAsync(cancellationToken);
        await _movements.SynchronizeAsync(State, cancellationToken);
    }

    /// <summary>
    /// Continues every train whose movement reaches beyond its current authority segment
    /// (<see cref="DispatchedTrainState.AuthorityLimitRouteIndex"/> before <see cref="DispatchedTrainState.MovementEndRouteIndex"/>)
    /// into its next segment, once that is possible (see <see cref="MovementSegmentation"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The next segment starts at the train's current block and is never implied by the train entering a block:
    /// it is an explicit new movement request with a new request ID, issued like a dispatcher re-plan (the
    /// current request and any prepared successor are cancelled first) and evaluated by the Track Controller from
    /// scratch, which sets the segment's switches before it grants anything. Handoff successors inside a
    /// segment still never pass its end.
    /// </para>
    /// <para>
    /// CTC continues only when the new segment reaches further than the current one and none of the switches it
    /// must move has a protection-zone block that is not clear (typically the train's own rear, still leaving the
    /// zone it passed on the way out). Until then the train keeps its current segment, whose end lies outside
    /// those zones, so it can never be held where it would lock a switch it needs. A failed send leaves the
    /// current segment in place and is retried on the next pass.
    /// </para>
    /// </remarks>
    private async Task ContinueIntoNextSegmentsAsync(CancellationToken cancellationToken)
    {
        foreach (var train in State.DispatchedTrains.ToList())
        {
            if (train.AuthorityLimitRouteIndex >= train.MovementEndRouteIndex
                || train.Phase != TrainMovementPhase.EnRoute
                || !MovementRequestPlanner.IsEligible(train, train.Phase)
                || State.FindLine(train.LineId) is not { } line
                || _movementsInFlight.Contains(train.TrainId))
            {
                continue;
            }

            var route = train.ActiveRouteBlockIds.ToList();
            int current = train.ActiveRouteIndex;
            int limit = train.AuthorityLimitRouteIndex;
            if (current < 0 || current >= route.Count || route[current] != train.CurrentBlockId || current > limit)
            {
                continue;
            }

            var topology = LineTopology.From(line);
            string? enteredFrom = current == 0 && train.PreviousBlockId.Length > 0 ? train.PreviousBlockId : null;
            int next = MovementSegmentation.SegmentEnd(topology, route, current, train.MovementEndRouteIndex, enteredFrom);
            string? waiting = next <= limit ? null : MovementSegmentation.ExtensionBlocker(line, topology, route, limit, next, enteredFrom);
            var request = next <= limit || waiting is not null
                ? null
                : MovementRequestPlanner.Plan(line, train.TrainId, route, current, next, train.PreviousBlockId, train.SuggestedSpeedMetersPerSecond);

            // Nothing beyond the current segment can be covered yet (e.g. the block after it is occupied).
            if (request is not null && current + request.PathBlockIds.Count <= limit)
            {
                request = null;
                waiting = $"block {route[limit + 1]} after the current segment is not safe to enter yet";
            }

            string trainName = TrainIds.DisplayName(train.TrainId);
            if (request is null)
            {
                string reason = waiting is null ? string.Empty : $"{trainName} waits to continue beyond {route[limit]}: {waiting}.";
                if (reason != train.NextSegmentWaitReason)
                {
                    train.NextSegmentWaitReason = reason;
                    if (reason.Length > 0)
                    {
                        OnStateChanged(CtcStateChangeKind.MovementRequestUpdated, train.CurrentBlockId, train.TrainId, reason);
                    }
                }

                continue;
            }

            _movementsInFlight.Add(train.TrainId);
            try
            {
                // Committed first so a pass that runs while this one awaits plans the same request; rolled back
                // if it could not be sent, so CTC never claims a segment it did not request.
                train.AuthorityLimitRouteIndex = next;
                try
                {
                    await _movements.ReplaceAsync(train, request, cancellationToken);
                }
                catch
                {
                    train.AuthorityLimitRouteIndex = limit;
                    throw;
                }

                train.NextSegmentWaitReason = string.Empty;
                train.SuggestedAuthorityMeters = request.AuthorityMeters;
                OnStateChanged(
                    CtcStateChangeKind.MovementRequestUpdated,
                    train.CurrentBlockId,
                    train.TrainId,
                    $"{trainName} continues into its next route segment: a new movement request from {route[current]} through {route[next]} "
                    + $"(route block {next + 1} of {route.Count}), beyond the previous segment's end at {route[limit]}.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                OnStateChanged(
                    CtcStateChangeKind.TrainMovementSuggestionFailed,
                    trainId: train.TrainId,
                    message: $"{trainName} is ready to continue beyond {route[limit]}, but its new movement request could not be sent: {ex.Message}");
            }
            finally
            {
                _movementsInFlight.Remove(train.TrainId);
            }
        }
    }

    /// <summary>
    /// The end of the first authority segment of a movement from route index <paramref name="start"/> whose
    /// authority may reach <paramref name="movementEnd"/> (see <see cref="MovementSegmentation"/>).
    /// </summary>
    private static int FirstSegmentEnd(CtcLineState line, IReadOnlyList<string> route, int start, int movementEnd, string? enteredFrom) =>
        movementEnd <= start
            ? movementEnd
            : MovementSegmentation.SegmentEnd(LineTopology.From(line), route, start, movementEnd, string.IsNullOrEmpty(enteredFrom) ? null : enteredFrom);

    public void ApplyTicketSales(TicketSalesMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Guard.NotNullOrWhiteSpace(message.LineId, nameof(message));

        var line = State.FindLine(message.LineId)
            ?? throw new ArgumentException($"Unknown line '{message.LineId}'.", nameof(message));

        line.TicketSalesPerHour = message.TicketsPerHour;

        OnStateChanged(CtcStateChangeKind.TicketSales);
    }

    /// <summary>
    /// Sets CTC's notion of the current time and releases every train that is now due.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CTC deliberately has no clock of its own: every module must agree on one simulation
    /// time, so the time is owned by the shared simulation clock (hosted by the Launcher; the
    /// TestUI during isolated development) and arrives here as SystemTimeMessages, about every
    /// 100 ms. CTC controls that clock from its UI, but never interpolates or advances time itself.
    /// </para>
    /// <para>
    /// A train is due when <c>DepartureTime &lt;= SystemTime</c>, not <c>==</c>: messages can
    /// be delayed or skipped (e.g. 12:00:07 then 12:00:09), and a train due at 12:00:08 must
    /// still leave. A jump forward therefore releases every train it passed, in departure order.
    /// </para>
    /// <para>
    /// Each due train is attempted independently: if one cannot be released (its start block is
    /// not safe), it stays queued and is retried on the next time update while the remaining due
    /// trains are still attempted. Moving the time backwards never un-dispatches a train; pending
    /// trains simply wait.
    /// </para>
    /// <para>
    /// A released train's physical dispatch is sent at once (and resent until it settles, see
    /// <see cref="TrainDispatchCoordinator"/>). A send failure never puts the train back in the queue or
    /// loses its dispatch: it is recorded and retried, so repeated clock ticks never create a second train.
    /// </para>
    /// </remarks>
    public async Task SetSystemTimeAsync(TimeSpan systemTime, CancellationToken cancellationToken = default)
    {
        State.SystemTime = systemTime;
        OnStateChanged(CtcStateChangeKind.SystemTime);

        // Claim every due entry up front so a dispatch pass started while this one is
        // awaiting a send (another time update, re-entrant on the UI thread) cannot release them again.
        var dueEntries = State.DispatchQueue
            .Where(entry => entry.QueueStatus == DispatchQueueStatus.Queued && entry.DepartureTime <= systemTime)
            .OrderBy(entry => entry.DepartureTime)
            .ToList();

        foreach (var entry in dueEntries)
        {
            entry.QueueStatus = DispatchQueueStatus.Dispatching;
        }

        foreach (var entry in dueEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string? warning = ReleaseTrain(entry);
                OnStateChanged(CtcStateChangeKind.TrainDispatched, trainId: entry.TrainId, message: warning);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not released: put the train back so the next time update retries it.
                entry.QueueStatus = DispatchQueueStatus.Queued;
                OnStateChanged(CtcStateChangeKind.DispatchFailed, trainId: entry.TrainId, message: ex.Message);
            }
        }

        // Released trains' physical dispatches go out now; failures are recorded and retried, never thrown.
        await _dispatches.SynchronizeAsync(State, cancellationToken);

        await AdvanceMovementPlansAsync(cancellationToken);
    }

    /// <summary>
    /// The time-driven half of the <c>Arrived → Dwelling → EnRoute</c> cycle: ends every dwell
    /// whose time has come and starts each ready train's next movement by issuing its new movement
    /// request (speed planned from the stop's departure to the next stop's arrival; authority along
    /// the unchanged active route). A failed send leaves the train ready and is retried on the next
    /// SystemTime update; other trains are still processed.
    /// </summary>
    private async Task AdvanceMovementPlansAsync(CancellationToken cancellationToken)
    {
        foreach (var train in State.DispatchedTrains.ToList())
        {
            // A send for this train is already awaiting (re-entrant time update); never send twice.
            if (!_movementsInFlight.Add(train.TrainId))
            {
                continue;
            }

            try
            {
                bool dwellEnded = MovementPlanner.CompleteDwellIfDue(train, State.SystemTime);
                bool ready = train.Phase == TrainMovementPhase.Arrived
                    || (train.Phase == TrainMovementPhase.EnRoute && train.IsLegSuggestionPending);

                if (ready)
                {
                    string message = await StartNextMovementAsync(train, cancellationToken);
                    OnStateChanged(CtcStateChangeKind.TrainMovementPhaseChanged, train.CurrentBlockId, train.TrainId, message);
                }
                else if (dwellEnded)
                {
                    OnStateChanged(CtcStateChangeKind.TrainMovementPhaseChanged, train.CurrentBlockId, train.TrainId, DescribePhase(train));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                OnStateChanged(
                    CtcStateChangeKind.TrainMovementSuggestionFailed,
                    trainId: train.TrainId,
                    message: $"{TrainIds.DisplayName(train.TrainId)} is ready to proceed to {NextTargetBlockId(train)}, but its movement request could not be sent: {ex.Message}");
            }
            finally
            {
                _movementsInFlight.Remove(train.TrainId);
            }
        }
    }

    /// <summary>
    /// Starts the train's next movement: issues its new movement request (replacing the previous
    /// one) and, only once that was sent, advances the plan: a train
    /// <see cref="TrainMovementPhase.Arrived"/> at a stop departs it toward the next stop; a train
    /// already en route without a planned leg gets one. This is where a movement's authority end is
    /// recalculated, along the remaining route from the train's current block.
    /// </summary>
    /// <returns>Dispatcher-readable description of the change, including any timing warning.</returns>
    private async Task<string> StartNextMovementAsync(DispatchedTrainState train, CancellationToken cancellationToken)
    {
        string trainName = TrainIds.DisplayName(train.TrainId);
        var line = State.FindLine(train.LineId)
            ?? throw new InvalidOperationException($"{trainName} is on unknown line '{train.LineId}'.");

        bool departing = train.Phase == TrainMovementPhase.Arrived;
        var target = train.Stops[departing ? 1 : 0];

        var route = MovementPlanner.ResolveRoute(train, line, train.ActiveRouteIndex, target.RouteIndex)
            ?? throw new InvalidOperationException($"{trainName}'s route to {target.BlockId} uses a block that is no longer in the track layout.");
        var remainingRoute = MovementPlanner.ResolveRoute(train, line, train.ActiveRouteIndex, train.ActiveRouteBlockIds.Count - 1)
            ?? throw new InvalidOperationException($"{trainName}'s route uses a block that is no longer in the track layout.");

        // The movement starts when the dwell ended (or now, if that is later): dwell shortens
        // the time left to reach the next stop, it never moves that stop's arrival.
        var departure = departing ? MovementPlanner.DepartureTimeAfter(train, 0) : State.SystemTime;
        var speed = SpeedPlanner.CalculateLegSpeed(route, departure, target.ArrivalTime, State.SystemTime);
        int authorizedBlocks = AuthorityManager.CountAuthorizedBlocks(remainingRoute, trainOccupiesStartBlock: true);
        int movementEnd = train.ActiveRouteIndex + authorizedBlocks - 1;
        int authorityLimit = FirstSegmentEnd(line, train.ActiveRouteBlockIds.ToList(), train.ActiveRouteIndex, movementEnd, train.ActiveRouteIndex == 0 ? train.PreviousBlockId : null);
        double authority = remainingRoute.Take(authorityLimit - train.ActiveRouteIndex + 1).Sum(block => block.LengthMeters);

        var request = MovementRequestPlanner.IsEligible(train, TrainMovementPhase.EnRoute)
            ? MovementRequestPlanner.Plan(line, train.TrainId, train.ActiveRouteBlockIds.ToList(), train.ActiveRouteIndex, authorityLimit, train.PreviousBlockId, speed.SpeedMetersPerSecond)
            : null;

        // As everywhere else, the plan changes only after the request was actually sent.
        await _movements.ReplaceAsync(train, request, cancellationToken);

        string fromBlockId = train.CurrentBlockId;
        if (departing)
        {
            train.Stops.RemoveAt(0);
            train.Phase = TrainMovementPhase.EnRoute;
            train.ActualArrivalTime = null;
        }

        train.IsLegSuggestionPending = false;
        train.SuggestedSpeedMetersPerSecond = speed.SpeedMetersPerSecond;
        train.SuggestedAuthorityMeters = authority;
        train.AuthorityLimitRouteIndex = authorityLimit;
        train.MovementEndRouteIndex = movementEnd;
        train.NextSegmentWaitReason = string.Empty;
        target.PlannedSpeedMetersPerSecond = speed.SpeedMetersPerSecond;
        target.TimingWarning = speed.ArrivalWarning;
        MovementPlanner.RecalculatePlannedSpeeds(train, line, State.SystemTime);

        string sent = request is null ? "no safe authority, so no movement request was issued" : "movement request sent";
        string message = departing
            ? $"{trainName} departed {fromBlockId} toward {target.BlockId} ({sent})."
            : $"{trainName} is travelling to {target.BlockId} ({sent}).";
        return speed.ArrivalWarning is { } warning ? $"{message} Warning: for {trainName}, {warning}" : message;
    }

    /// <summary>Block the train's next movement goes to: the stop after the current one when it is at a stop.</summary>
    private static string NextTargetBlockId(DispatchedTrainState train) =>
        train.Phase != TrainMovementPhase.EnRoute && train.Stops.Count > 1 ? train.Stops[1].BlockId : train.TargetBlockId;

    /// <summary>Dispatcher-readable description of a train's current phase.</summary>
    private static string DescribePhase(DispatchedTrainState train)
    {
        string trainName = TrainIds.DisplayName(train.TrainId);
        return train.Phase switch
        {
            TrainMovementPhase.Dwelling => string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                @"{0} arrived at {1} and is dwelling {2:0} s, until {3:hh\:mm\:ss}.",
                trainName,
                train.TargetBlockId,
                train.TargetDwellTime.TotalSeconds,
                train.ReadyToDepartTime),
            TrainMovementPhase.Arrived => $"{trainName} arrived at {train.TargetBlockId} and is ready to proceed to {NextTargetBlockId(train)}.",
            TrainMovementPhase.Completed => $"{trainName} completed its movement plan at {train.TargetBlockId}.",
            _ => $"{trainName} is travelling to {train.TargetBlockId}.",
        };
    }

    /// <summary>
    /// Releases <paramref name="entry"/>'s train: calculates its initial suggested speed and the
    /// fixed end of its first movement's authority, and moves it from the dispatch queue to the
    /// dispatched trains, awaiting placement.
    /// </summary>
    /// <remarks>
    /// Nothing is sent here. The caller then sends the train's physical dispatch
    /// (<see cref="TrainDispatchRequestMessage"/>: train, line, start block, ordered route) through
    /// <see cref="TrainDispatchCoordinator"/>; the legacy label-only <see cref="MovementRequestMessage"/>
    /// is never sent. The train stays <see cref="TrainReleaseStatus.AwaitingPlacement"/>, and its first
    /// complete movement request is issued only when the start block reports occupied (the Track
    /// Controller authorizes movement only for a train it sees in the request's target block).
    /// </remarks>
    /// <returns>A dispatcher-readable warning (e.g. the train is late), or null.</returns>
    /// <exception cref="DispatchException">The start block is not safe; nothing changed.</exception>
    private string? ReleaseTrain(DispatchQueueEntry entry)
    {
        var scheduledTrain = State.ScheduledTrains.FirstOrDefault(train => train.TrainId == entry.TrainId && train.LineId == entry.LineId)
            ?? throw new InvalidOperationException($"{TrainIds.DisplayName(entry.TrainId)} is queued but no longer scheduled.");

        var line = State.FindLine(scheduledTrain.LineId)
            ?? throw new InvalidOperationException($"{TrainIds.DisplayName(entry.TrainId)} is scheduled on unknown line '{scheduledTrain.LineId}'.");
        var route = ResolveRoute(scheduledTrain, line)
            ?? throw new InvalidOperationException($"{TrainIds.DisplayName(entry.TrainId)} uses a block that is no longer in the track layout.");

        // Releasing a train with zero authority would leave it stranded. Hold it in the queue
        // instead; it is retried next tick. Unknown occupancy is as unsafe as occupied.
        int authorizedBlocks = AuthorityManager.CountAuthorizedBlocks(route, trainOccupiesStartBlock: false);
        if (authorizedBlocks == 0)
        {
            throw new DispatchException($"its start block {route[0].BlockId} is occupied, has unknown occupancy, or is closed for maintenance.");
        }

        // A route that revisits blocks or sets a switch two ways is requested segment by segment.
        int movementEnd = authorizedBlocks - 1;
        int segmentEnd = FirstSegmentEnd(line, scheduledTrain.Route.Select(block => block.BlockId).ToList(), 0, movementEnd, null);
        double authority = route.Take(segmentEnd + 1).Sum(block => block.LengthMeters);
        var speed = SpeedPlanner.CalculateInitialSpeed(scheduledTrain, route, State.SystemTime);

        State.DispatchQueue.Remove(entry);

        var dispatched = State.FindDispatchedTrain(scheduledTrain.TrainId);
        if (dispatched is null)
        {
            dispatched = new DispatchedTrainState { TrainId = scheduledTrain.TrainId };
            State.DispatchedTrains.Add(dispatched);
        }

        dispatched.LineId = scheduledTrain.LineId;
        dispatched.DispatchRequest = null; // a fresh dispatch for this release
        dispatched.RouteUpdate = null;
        dispatched.TrackingStatus = string.Empty;
        dispatched.SuggestedSpeedMetersPerSecond = speed.SpeedMetersPerSecond;
        dispatched.SuggestedAuthorityMeters = authority;
        dispatched.AuthorityLimitRouteIndex = segmentEnd;
        dispatched.MovementEndRouteIndex = movementEnd;
        dispatched.NextSegmentWaitReason = string.Empty;

        // CTC knows where the train enters because it released it there: its starting block, a yard
        // entry checked when the schedule was queued. From now on the location follows sequential
        // occupancy along the active route (TrainLocationTracker).
        dispatched.CurrentBlockId = scheduledTrain.StartBlockId;
        dispatched.PreviousBlockId = string.Empty;
        dispatched.ActiveRouteIndex = 0;
        dispatched.CurrentBlockOccupancyObserved = false;
        ReplaceActiveRoute(dispatched, scheduledTrain.Route.Select(routeBlock => routeBlock.BlockId));

        // The Active Movement Plan starts as the schedule's timed blocks (with their dwell
        // times); its current stop, the schedule's next timed block, is what the speed was
        // planned toward. The dispatcher can change it later (UpdateMovementPlanStopAsync).
        dispatched.Stops.Clear();
        foreach (var stop in MovementPlanner.CreateStops(scheduledTrain))
        {
            dispatched.Stops.Add(stop);
        }

        dispatched.Phase = TrainMovementPhase.EnRoute;
        dispatched.ActualArrivalTime = null;
        dispatched.IsLegSuggestionPending = false;
        dispatched.Stops[0].PlannedSpeedMetersPerSecond = speed.SpeedMetersPerSecond;
        dispatched.Stops[0].TimingWarning = speed.ArrivalWarning;
        MovementPlanner.RecalculatePlannedSpeeds(dispatched, line, State.SystemTime);

        return speed.IsLate
            ? $"{TrainIds.DisplayName(scheduledTrain.TrainId)} is behind schedule for {speed.TargetBlockId}; suggested the maximum permitted speed."
            : null;
    }

    /// <summary>
    /// The line's blocks for <paramref name="train"/>'s route, aligned with <see cref="ScheduledTrain.Route"/>;
    /// null if any scheduled block is not on the line.
    /// </summary>
    private static List<CtcBlockState>? ResolveRoute(ScheduledTrain train, CtcLineState line)
    {
        var route = new List<CtcBlockState>();
        foreach (var routeBlock in train.Route)
        {
            var block = line.Blocks.FirstOrDefault(block => block.BlockId == routeBlock.BlockId);
            if (block is null)
            {
                return null;
            }

            route.Add(block);
        }

        return route;
    }

    /// <summary>
    /// Re-plans the CURRENT stop of a train that has already been released: the destination and
    /// arrival time the dispatcher chose for its next movement. The stop keeps its dwell when the
    /// destination is unchanged, and gets the new block's default dwell when it changes. See
    /// <see cref="UpdateMovementPlanStopAsync"/>.
    /// </summary>
    public Task<string?> UpdateDispatchedTrainPlanAsync(string trainId, string destinationBlockId, TimeSpan targetArrivalTime, CancellationToken cancellationToken = default)
    {
        var current = State.FindDispatchedTrain(trainId)?.CurrentStop;
        TimeSpan dwellTime = current is not null && current.BlockId == destinationBlockId
            ? current.DwellTime
            : State.FindBlock(destinationBlockId) is { } block ? DwellTimePolicy.GetDefaultDwellTime(block) : TimeSpan.Zero;

        return UpdateMovementPlanStopAsync(trainId, 0, destinationBlockId, targetArrivalTime, dwellTime, cancellationToken);
    }

    /// <summary>
    /// Changes one stop of a released train's Active Movement Plan (its destination block,
    /// arrival time and dwell time) and recalculates everything that depends on it. Only a new
    /// movement request is ever sent, never a release request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="stopIndex"/> 0 is the current stop (<see cref="DispatchedTrainState.CurrentStop"/>).
    /// Changing a stop's DESTINATION re-routes the movement into it (from the train's current
    /// block for stop 0, otherwise from the previous stop) with <see cref="RouteManager"/>; the
    /// changed stop becomes the end of the plan, so later stops of the old route are dropped. For
    /// stop 0 this re-plans the train from where it is, as before dwell times existed.
    /// </para>
    /// <para>
    /// Changing only the arrival or dwell time never changes the route. Dwell never moves the
    /// stop's own arrival; it delays the next movement, whose speed is planned from this stop's
    /// departure (<c>arrival + dwell</c>) to the next stop's arrival. A plan in which a stop is
    /// not entered after the train departs the previous one is rejected.
    /// </para>
    /// <para>
    /// The movement under way is re-planned only when it changes (stop 0's destination or arrival
    /// while en route: speed recalculated from now) or the route changes (authority end recalculated
    /// along the new route by <see cref="AuthorityManager"/>). For a train already on the track this
    /// replaces its movement request (the old one is cancelled first, the new one has a new request
    /// ID); a train still awaiting placement just keeps the new plan for its first request. Other
    /// edits, e.g. a dwell change, only update the planned timing. The train's state changes only
    /// after a needed send succeeded; the original schedule is never changed.
    /// </para>
    /// </remarks>
    /// <returns>A dispatcher-readable warning (e.g. an arrival time cannot be met), or null.</returns>
    /// <exception cref="ArgumentException">The train or block ID is blank.</exception>
    /// <exception cref="DispatchException">
    /// The train is not dispatched, its current block is unknown, the stop does not exist, the
    /// destination is not on its line or is where the movement starts, no route reaches it, a
    /// block on the route has no speed limit, the dwell is negative, or the times are impossible.
    /// Nothing was sent or changed.
    /// </exception>
    /// <exception cref="Exceptions.MessageSendException">The request could not be delivered; nothing changed.</exception>
    public async Task<string?> UpdateMovementPlanStopAsync(
        string trainId,
        int stopIndex,
        string destinationBlockId,
        TimeSpan arrivalTime,
        TimeSpan dwellTime,
        CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(trainId, nameof(trainId));
        Guard.NotNullOrWhiteSpace(destinationBlockId, nameof(destinationBlockId));

        string trainName = TrainIds.DisplayName(trainId);
        var train = State.FindDispatchedTrain(trainId)
            ?? throw new DispatchException($"{trainName} has not been dispatched.");

        var line = State.FindLine(train.LineId)
            ?? throw new DispatchException($"{trainName} is on unknown line '{train.LineId}'.");

        if (string.IsNullOrEmpty(train.CurrentBlockId)
            || !line.Blocks.Any(block => block.BlockId == train.CurrentBlockId)
            || train.ActiveRouteIndex < 0
            || train.ActiveRouteIndex >= train.ActiveRouteBlockIds.Count)
        {
            throw new DispatchException($"{trainName}'s current block is unknown, so no route can be planned from it.");
        }

        if (stopIndex < 0 || (stopIndex >= train.Stops.Count && !(stopIndex == 0 && train.Stops.Count == 0)))
        {
            throw new DispatchException($"{trainName}'s movement plan has no stop {stopIndex + 1}.");
        }

        if (!line.Blocks.Any(block => block.BlockId == destinationBlockId))
        {
            throw new DispatchException($"Block {destinationBlockId} is not on {line.Name}.");
        }

        if (DwellTimePolicy.Validate(dwellTime) is { } dwellError)
        {
            throw new DispatchException($"{trainName} at {destinationBlockId}: {dwellError}");
        }

        // Work on copies, normalized so the route starts at the current block, and commit only
        // after any needed send succeeded.
        int offset = train.ActiveRouteIndex;
        var routeIds = train.ActiveRouteBlockIds.Skip(offset).ToList();
        var stops = train.Stops.Select(stop => new MovementPlanStop
        {
            BlockId = stop.BlockId,
            ArrivalTime = stop.ArrivalTime,
            DwellTime = stop.DwellTime,
            RouteIndex = stop.RouteIndex - offset,
            PlannedSpeedMetersPerSecond = stop.PlannedSpeedMetersPerSecond,
            TimingWarning = stop.TimingWarning,
        }).ToList();

        bool destinationChanged = stopIndex >= stops.Count || stops[stopIndex].BlockId != destinationBlockId;
        bool arrivalChanged = destinationChanged || stops[stopIndex].ArrivalTime != arrivalTime;
        if (destinationChanged)
        {
            int fromIndex = stopIndex == 0 ? 0 : stops[stopIndex - 1].RouteIndex;
            string fromBlockId = routeIds[fromIndex];
            if (destinationBlockId == fromBlockId)
            {
                throw new DispatchException(stopIndex == 0
                    ? $"{trainName}'s current block is already {destinationBlockId}; choose a destination beyond it."
                    : $"{trainName}'s previous stop is already {destinationBlockId}; choose a different destination.");
            }

            // The train continues in the direction it is travelling: from the block it entered the start from.
            string? enteredFrom = fromIndex > 0 ? routeIds[fromIndex - 1] : train.PreviousBlockId;
            var path = RouteManager.FindRoute(line, fromBlockId, destinationBlockId, enteredFrom)
                ?? throw new DispatchException(stopIndex == 0
                    ? $"No route on {line.Name} connects {trainName}'s current block {fromBlockId} to {destinationBlockId}."
                    : $"No route on {line.Name} connects {trainName}'s stop {fromBlockId} to {destinationBlockId}.");

            routeIds = routeIds.Take(fromIndex).Concat(path.Select(block => block.BlockId)).ToList();
            stops = stops.Take(stopIndex).ToList();
            stops.Add(new MovementPlanStop
            {
                BlockId = destinationBlockId,
                ArrivalTime = arrivalTime,
                DwellTime = dwellTime,
                RouteIndex = routeIds.Count - 1,
            });
        }
        else
        {
            stops[stopIndex].ArrivalTime = arrivalTime;
            stops[stopIndex].DwellTime = dwellTime;
        }

        if (MovementPlanner.ValidateStopTiming(trainId, stops) is { } timingError)
        {
            throw new DispatchException(timingError);
        }

        // A new destination for the current stop re-plans the train from where it is. Otherwise a
        // train at its current stop stays there, dwelling for the (possibly changed) dwell time.
        var phase = train.Phase;
        var actualArrivalTime = train.ActualArrivalTime;
        if (stopIndex == 0 && destinationChanged)
        {
            phase = TrainMovementPhase.EnRoute;
            actualArrivalTime = null;
        }
        else if (stopIndex == 0 && phase != TrainMovementPhase.EnRoute)
        {
            var ready = actualArrivalTime + dwellTime;
            phase = ready is { } readyTime && State.SystemTime < readyTime
                ? TrainMovementPhase.Dwelling
                : stops.Count > 1 ? TrainMovementPhase.Arrived : TrainMovementPhase.Completed;
        }

        var routeBlocks = routeIds.Select(id => line.Blocks.First(block => block.BlockId == id)).ToList();
        // The movement under way ends at stop 0, so only its destination or arrival affects it;
        // its dwell only affects the movement after it.
        bool currentMovementChanged = stopIndex == 0 && phase == TrainMovementPhase.EnRoute && arrivalChanged;
        bool movementReplanned = currentMovementChanged || destinationChanged;

        SpeedSuggestion? speed = null;
        if (currentMovementChanged)
        {
            speed = SpeedPlanner.CalculateSpeedToTarget(routeBlocks.Take(stops[0].RouteIndex + 1).ToList(), stops[0].ArrivalTime, State.SystemTime);
        }

        // Authority follows the route; its end is recalculated (along the unchanged or new route) only
        // when the movement is re-planned anyway. The train occupies its own current block.
        int authorizedBlocks = movementReplanned ? AuthorityManager.CountAuthorizedBlocks(routeBlocks, trainOccupiesStartBlock: true) : 0;
        int movementEnd = movementReplanned ? authorizedBlocks - 1 : Math.Max(train.MovementEndRouteIndex, train.AuthorityLimitRouteIndex) - offset;
        int authorityLimit = movementReplanned ? FirstSegmentEnd(line, routeIds, 0, movementEnd, train.PreviousBlockId) : train.AuthorityLimitRouteIndex - offset;
        double authority = movementReplanned ? routeBlocks.Take(authorityLimit + 1).Sum(block => block.LengthMeters) : train.SuggestedAuthorityMeters;
        double suggestedSpeed = speed?.SpeedMetersPerSecond ?? train.SuggestedSpeedMetersPerSecond;

        if (movementReplanned && MovementRequestPlanner.IsEligible(train, phase))
        {
            var request = MovementRequestPlanner.Plan(line, train.TrainId, routeIds, 0, authorityLimit, train.PreviousBlockId, suggestedSpeed);

            // If sending throws, the exception propagates and the train's plan is deliberately
            // left untouched: CTC must not claim a movement it never requested.
            await _movements.ReplaceAsync(train, request, cancellationToken);
        }

        train.SuggestedSpeedMetersPerSecond = suggestedSpeed;
        train.SuggestedAuthorityMeters = authority;
        train.AuthorityLimitRouteIndex = authorityLimit;
        train.MovementEndRouteIndex = movementEnd;
        train.NextSegmentWaitReason = string.Empty;

        // From now on occupancy is interpreted along this route. It starts at the current block,
        // so the train stays where it is; whether that block has been seen occupied is unchanged.
        ReplaceActiveRoute(train, routeIds);
        train.ActiveRouteIndex = 0;

        // The track maps a placed train's position along its route: tell it the new one (a train still
        // awaiting placement gets a new dispatch for the new plan on the next pass instead).
        if (destinationChanged)
        {
            _dispatches.QueueRouteUpdate(train, routeIds);
        }

        train.Stops.Clear();
        foreach (var stop in stops)
        {
            train.Stops.Add(stop);
        }

        train.Phase = phase;
        train.ActualArrivalTime = actualArrivalTime;
        if (currentMovementChanged)
        {
            train.IsLegSuggestionPending = false;
            stops[0].PlannedSpeedMetersPerSecond = suggestedSpeed;
            stops[0].TimingWarning = speed?.ArrivalWarning;
        }

        var warnings = new List<string>();
        if (speed?.ArrivalWarning is { } arrivalWarning)
        {
            warnings.Add($"For {trainName}, {arrivalWarning}");
        }

        // Later movements: a changed arrival or dwell shifts the time available to the next stop.
        warnings.AddRange(MovementPlanner.RecalculatePlannedSpeeds(train, line, State.SystemTime));

        if (movementReplanned && authority <= 0)
        {
            warnings.Add($"{trainName}'s current block {train.CurrentBlockId} is closed for maintenance, so its suggested authority is 0.");
        }

        string? warning = warnings.Count == 0 ? null : string.Join(" ", warnings);
        OnStateChanged(CtcStateChangeKind.TrainMovementPlanUpdated, trainId: train.TrainId, message: warning);

        // Route update or re-dispatch: recorded and retried on failure, never thrown.
        await _dispatches.SynchronizeAsync(State, cancellationToken);
        return warning;
    }

    /// <summary>
    /// Stores a validated schedule and queues its trains for dispatch. Trains already
    /// scheduled on the same line(s) are replaced; other lines' schedules are kept.
    /// Everything is checked before any state changes, so a bad schedule is never partially queued.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A train is blank, not a supported train (only "001" to "010" can be dispatched),
    /// duplicated, uses an ID a train on another line already has (scheduled, queued or
    /// dispatched), is on an unknown line, has fewer than two route blocks or a block not on its
    /// line, does not start at one of its line's yard entries (<see cref="YardEntries"/>), or its schedule needs a speed above the permitted maximum
    /// (see <see cref="SpeedPlanner.ValidateFeasibility"/>).
    /// </exception>
    public void QueueSchedule(IEnumerable<ScheduledTrain> scheduledTrains)
    {
        ArgumentNullException.ThrowIfNull(scheduledTrains);

        var trains = scheduledTrains.ToList();
        var trainIds = new HashSet<string>();
        var replacedLineIds = trains.Where(train => train is not null).Select(train => train.LineId).ToHashSet();
        foreach (var train in trains)
        {
            if (train is null || string.IsNullOrWhiteSpace(train.TrainId))
            {
                throw new ArgumentException("Every scheduled train needs a train ID.", nameof(scheduledTrains));
            }

            // Only the ten fleet trains (001-010) exist downstream; anything else would be
            // accepted here and then rejected by the Train Controller. Dispatcher-readable, so no parameter name.
            if (!TrainIds.IsSupported(train.TrainId))
            {
                throw new ArgumentException(TrainIds.UnsupportedMessage(train.TrainId));
            }

            if (!trainIds.Add(train.TrainId))
            {
                throw new ArgumentException($"{TrainIds.DisplayName(train.TrainId)} is scheduled more than once.", nameof(scheduledTrains));
            }

            // Train IDs are unique across the whole system, not per line.
            string? otherLineId = FindOtherLineUsingTrainId(train, replacedLineIds);
            if (otherLineId is not null)
            {
                string otherLine = State.FindLine(otherLineId)?.Name ?? otherLineId;
                throw new ArgumentException(
                    $"{TrainIds.DisplayName(train.TrainId)} is already scheduled, queued or dispatched on {otherLine}. Train IDs must be unique across all lines.");
            }

            var line = State.FindLine(train.LineId)
                ?? throw new ArgumentException($"Unknown line '{train.LineId}'.", nameof(scheduledTrains));

            if (train.Route.Count < 2 || !train.Route[0].IsTimed || !train.Route[^1].IsTimed)
            {
                throw new ArgumentException($"{TrainIds.DisplayName(train.TrainId)} needs a timed route start block and at least one later timed block.", nameof(scheduledTrains));
            }

            // A train enters service only from one of its line's yard entries (Route[0] is its
            // starting block). Dispatcher-readable, so no parameter name.
            string? startError = YardEntries.Validate(line, train.StartBlockId, TrainIds.DisplayName(train.TrainId), State.Lines);
            if (startError is not null)
            {
                throw new ArgumentException(startError);
            }

            var route = ResolveRoute(train, line)
                ?? throw new ArgumentException($"{TrainIds.DisplayName(train.TrainId)} uses a block that is not on {line.Name}.", nameof(scheduledTrains));

            for (int i = 0; i + 1 < route.Count; i++)
            {
                if (!route[i].ConnectedBlockIds.Contains(route[i + 1].BlockId))
                {
                    throw new ArgumentException($"{TrainIds.DisplayName(train.TrainId)}'s route goes from {route[i].BlockId} to {route[i + 1].BlockId}, which are not connected.", nameof(scheduledTrains));
                }
            }

            // An impossible schedule is rejected outright; it is never accepted with its speed clamped.
            // No parameter name, so the message stays dispatcher-readable as it is.
            string? infeasible = SpeedPlanner.ValidateFeasibility(train, route);
            if (infeasible is not null)
            {
                throw new ArgumentException(infeasible);
            }
        }

        var lineIds = trains.Select(train => train.LineId).ToHashSet();
        foreach (var replaced in State.ScheduledTrains.Where(train => lineIds.Contains(train.LineId)).ToList())
        {
            State.ScheduledTrains.Remove(replaced);
        }

        foreach (var train in trains)
        {
            State.ScheduledTrains.Add(train);
        }

        // Rebuild only the pending part of the queue. Already-dispatched trains (recorded in
        // DispatchedTrains) are never queued again, and entries being released right now are
        // kept as they are so they cannot be released twice.
        var inFlight = State.DispatchQueue.Where(entry => entry.QueueStatus == DispatchQueueStatus.Dispatching).ToList();
        var pending = State.ScheduledTrains
            .Where(train => State.FindDispatchedTrain(train.TrainId) is null)
            .Where(train => !inFlight.Any(entry => entry.TrainId == train.TrainId && entry.LineId == train.LineId))
            .Select(train => new DispatchQueueEntry
            {
                TrainId = train.TrainId,
                LineId = train.LineId,
                DepartureTime = train.DepartureTime,
            });

        var rebuilt = inFlight.Concat(pending).OrderBy(entry => entry.DepartureTime).ToList();
        State.DispatchQueue.Clear();
        foreach (var entry in rebuilt)
        {
            State.DispatchQueue.Add(entry);
        }

        OnStateChanged(CtcStateChangeKind.Schedule);
        OnStateChanged(CtcStateChangeKind.DispatchQueue);
    }

    /// <summary>
    /// The line of another train that already uses <paramref name="train"/>'s ID and will still
    /// exist once this schedule is queued, or null. Scheduled and pending trains on
    /// <paramref name="replacedLineIds"/> are about to be replaced, so they do not count; trains
    /// already dispatched or being released are never replaced, so they always do.
    /// </summary>
    private string? FindOtherLineUsingTrainId(ScheduledTrain train, IReadOnlySet<string> replacedLineIds)
    {
        bool OtherLine(string lineId) => lineId != train.LineId;

        return State.DispatchedTrains
                .Where(dispatched => dispatched.TrainId == train.TrainId && OtherLine(dispatched.LineId))
                .Select(dispatched => dispatched.LineId)
            .Concat(State.DispatchQueue
                .Where(entry => entry.TrainId == train.TrainId && OtherLine(entry.LineId)
                    && (entry.QueueStatus == DispatchQueueStatus.Dispatching || !replacedLineIds.Contains(entry.LineId)))
                .Select(entry => entry.LineId))
            .Concat(State.ScheduledTrains
                .Where(scheduled => scheduled.TrainId == train.TrainId && OtherLine(scheduled.LineId) && !replacedLineIds.Contains(scheduled.LineId))
                .Select(scheduled => scheduled.LineId))
            .FirstOrDefault();
    }

    /// <summary>
    /// Builds a maintenance request for a known block, with a new request ID that the Track
    /// Controller's acknowledgement will carry. Does not change CTC state; the requested state is
    /// recorded only once a request has actually been sent (see <see cref="CloseBlockAsync"/> and
    /// <see cref="ReopenBlockAsync"/>).
    /// </summary>
    public MaintenanceRequestMessage CreateMaintenanceRequest(string blockId, MaintenanceState requestedState)
    {
        GetBlock(blockId);

        return new MaintenanceRequestMessage
        {
            RequestId = NewRequestId(),
            BlockId = blockId,
            RequestedState = requestedState,
        };
    }

    public SwitchPositionRequestMessage CreateSwitchPositionRequest(string blockId, SwitchPosition requestedPosition)
    {
        var block = GetBlock(blockId);
        if (!block.HasSwitch)
        {
            throw new ArgumentException($"Block '{blockId}' has no switch.", nameof(blockId));
        }

        if (requestedPosition == SwitchPosition.Unknown)
        {
            throw new ArgumentException("A specific switch position must be requested.", nameof(requestedPosition));
        }

        return new SwitchPositionRequestMessage
        {
            RequestId = NewRequestId(),
            BlockId = blockId,
            RequestedPosition = requestedPosition,
        };
    }

    /// <summary>
    /// Builds the legacy label-only release intent. Never sent: physical dispatch uses
    /// <see cref="TrainDispatchRequestMessage"/> (see <see cref="TrainDispatchCoordinator"/>), and the
    /// Track Controller rejects this message. Kept for compatibility.
    /// Switch changes are requested separately via <see cref="CreateSwitchPositionRequest"/>.
    /// </summary>
    public MovementRequestMessage CreateMovementRequest(string trainId)
    {
        Guard.NotNullOrWhiteSpace(trainId, nameof(trainId));

        return new MovementRequestMessage
        {
            TrainId = trainId,
            RequestType = MovementRequestType.ReleaseTrain,
        };
    }

    /// <summary>
    /// Sends a request to the Track Controller to close a block for maintenance.
    /// </summary>
    /// <exception cref="ArgumentException">The block ID is blank or unknown to CTC.</exception>
    /// <exception cref="Exceptions.MessageSendException">The request could not be delivered.</exception>
    public async Task CloseBlockAsync(string blockId, CancellationToken cancellationToken = default)
    {
        var block = GetBlock(blockId);
        var request = CreateMaintenanceRequest(blockId, MaintenanceState.Closed);

        // If sending throws, the exception propagates and the block's requested state
        // is deliberately left untouched: CTC must not claim a request it never issued.
        await _messageSender.SendAsync(request, cancellationToken);

        // RequestedMaintenanceState == Closed means "CTC successfully issued a Close
        // request". It does NOT mean the Track Controller confirmed the block is closed;
        // ConfirmedMaintenanceState changes only when a BlockStatusMessage reports it.
        block.RequestedMaintenanceState = MaintenanceState.Closed;
        block.PendingMaintenanceRequestId = request.RequestId;

        OnStateChanged(CtcStateChangeKind.MaintenanceRequest, block.BlockId);
    }

    /// <summary>
    /// Sends a request to the Track Controller to reopen a block closed for maintenance.
    /// </summary>
    /// <exception cref="ArgumentException">The block ID is blank or unknown to CTC.</exception>
    /// <exception cref="Exceptions.MessageSendException">The request could not be delivered.</exception>
    public async Task ReopenBlockAsync(string blockId, CancellationToken cancellationToken = default)
    {
        var block = GetBlock(blockId);
        var request = CreateMaintenanceRequest(blockId, MaintenanceState.Open);

        // As with CloseBlockAsync, a failed send leaves the requested state untouched.
        await _messageSender.SendAsync(request, cancellationToken);

        // The block stays confirmed Closed (and unsafe for authority) until the Track
        // Controller reports it Open via ApplyBlockStatus.
        block.RequestedMaintenanceState = MaintenanceState.Open;
        block.PendingMaintenanceRequestId = request.RequestId;

        OnStateChanged(CtcStateChangeKind.MaintenanceRequest, block.BlockId);
    }

    /// <summary>
    /// Sends a request to the Track Controller to move a block's switch.
    /// </summary>
    /// <exception cref="ArgumentException">The block is unknown, has no switch, or the position is Unknown.</exception>
    /// <exception cref="Exceptions.MessageSendException">The request could not be delivered.</exception>
    public async Task SetSwitchPositionAsync(string blockId, SwitchPosition requestedPosition, CancellationToken cancellationToken = default)
    {
        var request = CreateSwitchPositionRequest(blockId, requestedPosition);

        // The block's Switch is deliberately left untouched, even on success: it is the
        // wayside-reported position, which changes only through ApplyBlockStatus once the
        // Track Controller has actually moved the switch.
        await _messageSender.SendAsync(request, cancellationToken);

        GetBlock(blockId).PendingSwitchRequestId = request.RequestId;
    }

    private static string NewRequestId() => Guid.NewGuid().ToString("N");

    /// <summary>The block whose outstanding request this acknowledgement answers; null when stale.</summary>
    private CtcBlockState? FindPendingBlock(RequestAcknowledgementMessage message, Func<CtcBlockState, string> pendingRequestId)
    {
        if (string.IsNullOrEmpty(message.RequestId) || string.IsNullOrEmpty(message.BlockId))
        {
            return null;
        }

        var block = State.FindBlock(message.BlockId);
        return block is not null && pendingRequestId(block) == message.RequestId ? block : null;
    }

    private static void ReplaceActiveRoute(DispatchedTrainState train, IEnumerable<string> blockIds)
    {
        var route = blockIds.ToList();
        train.ActiveRouteBlockIds.Clear();
        foreach (var blockId in route)
        {
            train.ActiveRouteBlockIds.Add(blockId);
        }
    }

    private void OnStateChanged(CtcStateChangeKind kind, string? blockId = null, string? trainId = null, string? message = null) =>
        StateChanged?.Invoke(this, new CtcStateChangedEventArgs(kind, blockId, trainId, message));

    private CtcBlockState GetBlock(string blockId)
    {
        Guard.NotNullOrWhiteSpace(blockId, nameof(blockId));

        return State.FindBlock(blockId) ?? throw new ArgumentException($"Unknown block '{blockId}'.", nameof(blockId));
    }
}
