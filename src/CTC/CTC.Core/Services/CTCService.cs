using CTC.Core.Dispatching;
using CTC.Core.Exceptions;
using CTC.Core.Interfaces;
using CTC.Core.Models;
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
/// <see cref="TrainLocationTracker"/>. Suggestions are calculated at dispatch and when
/// the dispatcher re-plans a released train (see <see cref="UpdateDispatchedTrainPlanAsync"/>);
/// never automatically as trains move.
/// </summary>
/// <remarks>
/// Async methods resume on the caller's synchronization context, so when called from the
/// UI thread every state change also happens on the UI thread.
/// </remarks>
public class CTCService : ICTCService
{
    private readonly IMessageSender _messageSender;

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
            TrainId = scheduledTrain.TrainId,
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

        // The active movement target starts as the schedule's next timed block; the
        // dispatcher can change it later (UpdateDispatchedTrainPlanAsync).
        dispatched.TargetBlockId = speed.TargetBlockId;
        dispatched.TargetArrivalTime = speed.TargetArrivalTime;

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
    /// Re-plans a train that has already been released: routes it from its current block to
    /// <paramref name="destinationBlockId"/>, recalculates the suggested speed (to arrive at
    /// <paramref name="targetArrivalTime"/>) and authority along that route, and sends ONLY a new
    /// MovementSuggestion. No MovementRequest is sent: the train was already released.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The route always starts at <see cref="DispatchedTrainState.CurrentBlockId"/>, the block CTC
    /// has inferred from sequential track occupancy (see <see cref="TrainLocationTracker"/>).
    /// </para>
    /// <para>
    /// The train's state is updated only after the suggestion was sent, so if sending fails CTC
    /// keeps showing the plan it last actually issued. On success the new route also becomes the
    /// train's active route, so later occupancy is interpreted along it. The original schedule is
    /// not changed.
    /// </para>
    /// </remarks>
    /// <returns>A dispatcher-readable warning (e.g. the arrival time cannot be met), or null.</returns>
    /// <exception cref="ArgumentException">The train or block ID is blank.</exception>
    /// <exception cref="DispatchException">
    /// The train is not dispatched, its current block is unknown, the destination is not on
    /// its line or is its current block, no route reaches it, or a block on the route has no
    /// speed limit. Nothing was sent or changed.
    /// </exception>
    /// <exception cref="Exceptions.MessageSendException">The suggestion could not be delivered; nothing changed.</exception>
    public async Task<string?> UpdateDispatchedTrainPlanAsync(string trainId, string destinationBlockId, TimeSpan targetArrivalTime, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(trainId, nameof(trainId));
        Guard.NotNullOrWhiteSpace(destinationBlockId, nameof(destinationBlockId));

        string trainName = TrainIds.DisplayName(trainId);
        var train = State.FindDispatchedTrain(trainId)
            ?? throw new DispatchException($"{trainName} has not been dispatched.");

        var line = State.FindLine(train.LineId)
            ?? throw new DispatchException($"{trainName} is on unknown line '{train.LineId}'.");

        if (string.IsNullOrEmpty(train.CurrentBlockId) || !line.Blocks.Any(block => block.BlockId == train.CurrentBlockId))
        {
            throw new DispatchException($"{trainName}'s current block is unknown, so no route can be planned from it.");
        }

        if (!line.Blocks.Any(block => block.BlockId == destinationBlockId))
        {
            throw new DispatchException($"Block {destinationBlockId} is not on {line.Name}.");
        }

        if (destinationBlockId == train.CurrentBlockId)
        {
            throw new DispatchException($"{trainName}'s current block is already {destinationBlockId}; choose a destination beyond it.");
        }

        var route = RouteManager.FindRoute(line, train.CurrentBlockId, destinationBlockId)
            ?? throw new DispatchException($"No route on {line.Name} connects {trainName}'s current block {train.CurrentBlockId} to {destinationBlockId}.");

        var speed = SpeedPlanner.CalculateSpeedToTarget(route, targetArrivalTime, State.SystemTime);

        // The train is assumed to be the occupant of its own current block.
        double authority = AuthorityManager.CalculateUpdatedAuthorityMeters(route);

        var suggestion = new MovementSuggestionMessage
        {
            TrainId = train.TrainId,
            SuggestedSpeedMetersPerSecond = speed.SpeedMetersPerSecond,
            SuggestedAuthorityMeters = authority,
        };

        // If sending throws, the exception propagates and the train's plan is deliberately
        // left untouched: CTC must not claim a suggestion it never issued.
        await _messageSender.SendAsync(suggestion, cancellationToken);

        train.TargetBlockId = destinationBlockId;
        train.TargetArrivalTime = targetArrivalTime;
        train.SuggestedSpeedMetersPerSecond = suggestion.SuggestedSpeedMetersPerSecond;
        train.SuggestedAuthorityMeters = suggestion.SuggestedAuthorityMeters;

        // From now on occupancy is interpreted along the route CTC just issued. The route starts
        // at the current block, so the train stays where it is; whether that block has been seen
        // occupied is unchanged.
        ReplaceActiveRoute(train, route.Select(block => block.BlockId));
        train.ActiveRouteIndex = 0;

        var warnings = new List<string>();
        if (speed.ArrivalWarning is { } arrivalWarning)
        {
            warnings.Add($"For {trainName}, {arrivalWarning}");
        }

        if (authority <= 0)
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
