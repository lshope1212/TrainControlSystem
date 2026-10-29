using CTC.Core.Dispatching;
using CTC.Core.Exceptions;
using CTC.Core.Interfaces;
using CTC.Core.Models;
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
/// <see cref="MovementPlanner"/>. Suggestions are calculated at dispatch, when the
/// dispatcher re-plans a released train (see <see cref="UpdateMovementPlanStopAsync"/>), and
/// when a train finishes dwelling at a stop and starts its next movement; never just
/// because a train moved from one block to the next.
/// </summary>
/// <remarks>
/// Async methods resume on the caller's synchronization context, so when called from the
/// UI thread every state change also happens on the UI thread.
/// </remarks>
public class CTCService : ICTCService
{
    private readonly IMessageSender _messageSender;

    // Trains whose next-movement suggestion is being sent right now (see AdvanceMovementPlansAsync).
    private readonly HashSet<string> _movementsInFlight = new HashSet<string>();

    public CTCService(IMessageSender messageSender)
    {
        _messageSender = messageSender ?? throw new ArgumentNullException(nameof(messageSender));
    }

    public CtcSystemState State { get; } = new CtcSystemState();

    public event EventHandler<CtcStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Replaces CTC's territory with the lines and blocks in the layout.
    /// </summary>
    public void ApplyTrackLayout(TrackLayoutMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        State.Lines.Clear();

        foreach (var lineDefinition in message.Lines)
        {
            var line = new CtcLineState
            {
                LineId = lineDefinition.LineId,
                Name = lineDefinition.Name,
            };

            foreach (var blockDefinition in lineDefinition.Blocks)
            {
                var block = new CtcBlockState
                {
                    BlockId = blockDefinition.BlockId,
                    BlockNumber = blockDefinition.BlockNumber,
                    Section = blockDefinition.Section,
                    LengthMeters = blockDefinition.LengthMeters,
                    SpeedLimitKilometersPerHour = blockDefinition.SpeedLimitKilometersPerHour,
                    StationName = blockDefinition.StationName,
                    HasSwitch = blockDefinition.HasSwitch,
                    HasSignal = blockDefinition.HasSignal,
                    HasCrossing = blockDefinition.HasCrossing,
                };

                foreach (var connectedBlockId in blockDefinition.ConnectedBlockIds)
                {
                    block.ConnectedBlockIds.Add(connectedBlockId);
                }

                line.Blocks.Add(block);
            }

            State.Lines.Add(line);
        }

        OnStateChanged(CtcStateChangeKind.TrackLayout);
    }

    /// <summary>
    /// Records the Track Controller's latest report for a block, then lets
    /// <see cref="TrainLocationTracker"/> infer whether a dispatched train moved into it.
    /// The report never identifies a train; occupancy that no single train explains stays anonymous.
    /// </summary>
    public void ApplyBlockStatus(BlockStatusMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        CtcBlockState block = GetBlock(message.BlockId);

        // Captured first: only a change INTO Occupied can move a train, never a repeated snapshot.
        OccupancyState previousOccupancy = block.Occupancy;
        block.Occupancy = message.Occupancy;
        block.Signal = message.Signal;
        block.Switch = message.Switch;
        block.Crossing = message.Crossing;

        // Only the confirmed state: RequestedMaintenanceState records what CTC asked for
        // and is never overwritten by a wayside report.
        block.ConfirmedMaintenanceState = message.Maintenance;

        OnStateChanged(CtcStateChangeKind.BlockStatus, block.BlockId);

        var location = TrainLocationTracker.ApplyOccupancy(State, block, previousOccupancy);
        if (location.AdvancedTrain is { } advanced)
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
    }

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
    /// time, so the time is owned by the external system clock (the TestUI during isolated
    /// development) and arrives here as SystemTimeMessages. CTC never interpolates between them.
    /// </para>
    /// <para>
    /// A train is due when <c>DepartureTime &lt;= SystemTime</c>, not <c>==</c>: messages can
    /// be delayed or skipped (e.g. 12:00:07 then 12:00:09), and a train due at 12:00:08 must
    /// still leave. A jump forward therefore releases every train it passed, in departure order.
    /// </para>
    /// <para>
    /// Each due train is attempted independently: if one send fails, that train stays queued
    /// (and is retried on the next time update) while the remaining due trains are still attempted.
    /// Moving the time backwards never un-dispatches a train; pending trains simply wait.
    /// </para>
    /// </remarks>
    public async Task SetSystemTimeAsync(TimeSpan systemTime, CancellationToken cancellationToken = default)
    {
        State.SystemTime = systemTime;
        OnStateChanged(CtcStateChangeKind.SystemTime);

        // Claim every due entry up front so a dispatch pass started while this one is
        // awaiting a send (another time update, re-entrant on the UI thread) cannot send them again.
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
            try
            {
                string? warning = await DispatchTrainAsync(entry, cancellationToken);
                OnStateChanged(CtcStateChangeKind.TrainDispatched, trainId: entry.TrainId, message: warning);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not dispatched: put the train back so the next time update retries it.
                entry.QueueStatus = DispatchQueueStatus.Queued;
                OnStateChanged(CtcStateChangeKind.DispatchFailed, trainId: entry.TrainId, message: ex.Message);
            }
            catch (OperationCanceledException)
            {
                // Shutting down: release every claim that has not completed, then stop.
                foreach (var pending in dueEntries.Where(pending => pending.QueueStatus == DispatchQueueStatus.Dispatching))
                {
                    pending.QueueStatus = DispatchQueueStatus.Queued;
                }

                throw;
            }
        }

        await AdvanceMovementPlansAsync(cancellationToken);
    }

    /// <summary>
    /// The time-driven half of the <c>Arrived → Dwelling → EnRoute</c> cycle: ends every dwell
    /// whose time has come and starts each ready train's next movement by sending its
    /// MovementSuggestion (speed planned from the stop's departure to the next stop's arrival;
    /// authority along the unchanged active route). A failed send leaves the train ready and is
    /// retried on the next SystemTime update; other trains are still processed.
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
                    message: $"{TrainIds.DisplayName(train.TrainId)} is ready to proceed to {NextTargetBlockId(train)}, but its movement suggestion could not be sent: {ex.Message}");
            }
            finally
            {
                _movementsInFlight.Remove(train.TrainId);
            }
        }
    }

    /// <summary>
    /// Sends the MovementSuggestion for the train's next movement and, only once it was sent,
    /// advances the plan: a train <see cref="TrainMovementPhase.Arrived"/> at a stop departs it
    /// toward the next stop; a train already en route without a sent suggestion gets one.
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
        double authority = AuthorityManager.CalculateUpdatedAuthorityMeters(remainingRoute);

        var suggestion = new MovementSuggestionMessage
        {
            TrainId = train.TrainId,
            SuggestedSpeedMetersPerSecond = speed.SpeedMetersPerSecond,
            SuggestedAuthorityMeters = authority,
        };

        // As everywhere else, the plan changes only after the suggestion was actually sent.
        await _messageSender.SendAsync(suggestion, cancellationToken);

        string fromBlockId = train.CurrentBlockId;
        if (departing)
        {
            train.Stops.RemoveAt(0);
            train.Phase = TrainMovementPhase.EnRoute;
            train.ActualArrivalTime = null;
        }

        train.IsLegSuggestionPending = false;
        train.SuggestedSpeedMetersPerSecond = suggestion.SuggestedSpeedMetersPerSecond;
        train.SuggestedAuthorityMeters = suggestion.SuggestedAuthorityMeters;
        target.PlannedSpeedMetersPerSecond = suggestion.SuggestedSpeedMetersPerSecond;
        target.TimingWarning = speed.ArrivalWarning;
        MovementPlanner.RecalculatePlannedSpeeds(train, line, State.SystemTime);

        string message = departing
            ? $"{trainName} departed {fromBlockId} toward {target.BlockId} (movement suggestion sent)."
            : $"{trainName} is travelling to {target.BlockId} (movement suggestion sent).";
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
    /// Releases <paramref name="entry"/>'s train: calculates its initial suggested speed and
    /// authority, sends the MovementSuggestion and then the MovementRequest to the Track
    /// Controller, and only then moves the train from the dispatch queue to the dispatched trains.
    /// </summary>
    /// <returns>A dispatcher-readable warning (e.g. the train is late), or null.</returns>
    /// <exception cref="DispatchException">The start block is unsafe; nothing was sent or changed.</exception>
    /// <exception cref="MessageSendException">A message could not be delivered; nothing changed.</exception>
    private async Task<string?> DispatchTrainAsync(DispatchQueueEntry entry, CancellationToken cancellationToken)
    {
        var scheduledTrain = State.ScheduledTrains.FirstOrDefault(train => train.TrainId == entry.TrainId && train.LineId == entry.LineId)
            ?? throw new InvalidOperationException($"{TrainIds.DisplayName(entry.TrainId)} is queued but no longer scheduled.");

        var line = State.FindLine(scheduledTrain.LineId)
            ?? throw new InvalidOperationException($"{TrainIds.DisplayName(entry.TrainId)} is scheduled on unknown line '{scheduledTrain.LineId}'.");
        var route = ResolveRoute(scheduledTrain, line)
            ?? throw new InvalidOperationException($"{TrainIds.DisplayName(entry.TrainId)} uses a block that is no longer in the track layout.");

        // Releasing a train with zero authority would leave it stranded, because CTC does not
        // recalculate authority automatically. Hold it in the queue instead; it is retried next tick.
        double authority = AuthorityManager.CalculateInitialAuthorityMeters(route);
        if (authority <= 0)
        {
            throw new DispatchException($"its start block {route[0].BlockId} is occupied or closed for maintenance.");
        }

        var speed = SpeedPlanner.CalculateInitialSpeed(scheduledTrain, route, State.SystemTime);

        var suggestion = new MovementSuggestionMessage
        {
            RequestId = Guid.NewGuid().ToString("N"),
            TrainId = scheduledTrain.TrainId,
            TargetBlockId = route[0].BlockId,
            PathBlockIds = route.Skip(1).Select(block => block.BlockId).ToList(),
            SuggestedSpeedMetersPerSecond = speed.SpeedMetersPerSecond,
            SuggestedAuthorityMeters = authority,
        };
        var request = CreateMovementRequest(scheduledTrain.TrainId);

        // Suggestion first, so the Track Controller has movement data before the release.
        // If the release then fails, the train stays queued and the retry sends both again.
        await _messageSender.SendAsync(suggestion, cancellationToken);
        await _messageSender.SendAsync(request, cancellationToken);

        // Only now, after both sends succeeded, does the train leave the queue. Removing it first
        // would make CTC believe a train was released when the request never reached the wayside.
        State.DispatchQueue.Remove(entry);

        var dispatched = State.FindDispatchedTrain(scheduledTrain.TrainId);
        if (dispatched is null)
        {
            dispatched = new DispatchedTrainState { TrainId = scheduledTrain.TrainId };
            State.DispatchedTrains.Add(dispatched);
        }

        dispatched.LineId = scheduledTrain.LineId;
        dispatched.SuggestedSpeedMetersPerSecond = suggestion.SuggestedSpeedMetersPerSecond;
        dispatched.SuggestedAuthorityMeters = suggestion.SuggestedAuthorityMeters;

        // CTC knows where the train enters because it released it there. From now on the
        // location follows sequential occupancy along the active route (TrainLocationTracker).
        // TODO: real route/yard logic will replace the temporary route-start rule.
        dispatched.CurrentBlockId = scheduledTrain.StartBlockId;
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
    /// MovementSuggestion is ever sent, never another MovementRequest.
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
    /// A MovementSuggestion is sent only when the movement under way changes (stop 0's
    /// destination or arrival while en route: speed recalculated from now) or the route changes (authority recalculated along
    /// the new route by <see cref="AuthorityManager"/>). Other edits, e.g. a dwell change, only
    /// update the planned timing; that speed is sent when the train starts that movement.
    /// The train's state changes only after a needed send succeeded; the original schedule is
    /// never changed.
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
    /// <exception cref="Exceptions.MessageSendException">The suggestion could not be delivered; nothing changed.</exception>
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

            var path = RouteManager.FindRoute(line, fromBlockId, destinationBlockId)
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
        bool sendNeeded = currentMovementChanged || destinationChanged;

        SpeedSuggestion? speed = null;
        if (currentMovementChanged)
        {
            speed = SpeedPlanner.CalculateSpeedToTarget(routeBlocks.Take(stops[0].RouteIndex + 1).ToList(), stops[0].ArrivalTime, State.SystemTime);
        }

        // Authority follows the route; it is recalculated (along the unchanged or new route) only
        // when a suggestion has to be sent anyway. The train occupies its own current block.
        double authority = sendNeeded ? AuthorityManager.CalculateUpdatedAuthorityMeters(routeBlocks) : train.SuggestedAuthorityMeters;
        double suggestedSpeed = speed?.SpeedMetersPerSecond ?? train.SuggestedSpeedMetersPerSecond;

        if (sendNeeded)
        {
            var suggestion = new MovementSuggestionMessage
            {
                TrainId = train.TrainId,
                SuggestedSpeedMetersPerSecond = suggestedSpeed,
                SuggestedAuthorityMeters = authority,
            };

            // If sending throws, the exception propagates and the train's plan is deliberately
            // left untouched: CTC must not claim a suggestion it never issued.
            await _messageSender.SendAsync(suggestion, cancellationToken);
        }

        train.SuggestedSpeedMetersPerSecond = suggestedSpeed;
        train.SuggestedAuthorityMeters = authority;

        // From now on occupancy is interpreted along this route. It starts at the current block,
        // so the train stays where it is; whether that block has been seen occupied is unchanged.
        ReplaceActiveRoute(train, routeIds);
        train.ActiveRouteIndex = 0;

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

        if (sendNeeded && authority <= 0)
        {
            warnings.Add($"{trainName}'s current block {train.CurrentBlockId} is closed for maintenance, so its suggested authority is 0.");
        }

        string? warning = warnings.Count == 0 ? null : string.Join(" ", warnings);
        OnStateChanged(CtcStateChangeKind.TrainMovementPlanUpdated, trainId: train.TrainId, message: warning);
        return warning;
    }

    /// <summary>
    /// Stores a validated schedule and queues its trains for dispatch. Trains already
    /// scheduled on the same line(s) are replaced; other lines' schedules are kept.
    /// Everything is checked before any state changes, so a bad schedule is never partially queued.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A train is blank, duplicated, on an unknown line, has fewer than two route blocks or a
    /// block not on its line, or its schedule needs a speed above the permitted maximum
    /// (see <see cref="SpeedPlanner.ValidateFeasibility"/>).
    /// </exception>
    public void QueueSchedule(IEnumerable<ScheduledTrain> scheduledTrains)
    {
        ArgumentNullException.ThrowIfNull(scheduledTrains);

        var trains = scheduledTrains.ToList();
        var trainIds = new HashSet<string>();
        foreach (var train in trains)
        {
            if (train is null || string.IsNullOrWhiteSpace(train.TrainId))
            {
                throw new ArgumentException("Every scheduled train needs a train ID.", nameof(scheduledTrains));
            }

            if (!trainIds.Add(train.TrainId))
            {
                throw new ArgumentException($"{TrainIds.DisplayName(train.TrainId)} is scheduled more than once.", nameof(scheduledTrains));
            }

            var line = State.FindLine(train.LineId)
                ?? throw new ArgumentException($"Unknown line '{train.LineId}'.", nameof(scheduledTrains));

            if (train.Route.Count < 2 || !train.Route[0].IsTimed || !train.Route[^1].IsTimed)
            {
                throw new ArgumentException($"{TrainIds.DisplayName(train.TrainId)} needs a timed route start block and at least one later timed block.", nameof(scheduledTrains));
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
        // DispatchedTrains) are never queued again, and entries whose MovementRequest is being
        // sent right now are kept as they are so they cannot be sent twice.
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
    /// Builds a maintenance request for a known block. Does not change CTC state; the
    /// requested state is recorded only once a request has actually been sent
    /// (see <see cref="CloseBlockAsync"/> and <see cref="ReopenBlockAsync"/>).
    /// </summary>
    public MaintenanceRequestMessage CreateMaintenanceRequest(string blockId, MaintenanceState requestedState)
    {
        GetBlock(blockId);

        return new MaintenanceRequestMessage
        {
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
            BlockId = blockId,
            RequestedPosition = requestedPosition,
        };
    }

    /// <summary>
    /// Builds a request to release a train for movement. Switch changes are requested
    /// separately via <see cref="CreateSwitchPositionRequest"/>.
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
