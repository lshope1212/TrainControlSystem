using CTC.Core.Interfaces;
using CTC.Core.Models;
using TrainControl.Common.Validation;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace CTC.Core.Services;

/// <summary>
/// CTC office service. Maps shared contract messages into <see cref="CtcSystemState"/>
/// and builds outbound contract messages. Transmission is delegated to an injected
/// <see cref="IMessageSender"/>; this class knows nothing about the transport.
/// Dispatching is time-triggered only (see <see cref="SetSystemTimeAsync"/>); there are no
/// routing or authority algorithms yet (see RouteManager / AuthorityManager).
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

    public void ApplyBlockStatus(BlockStatusMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        CtcBlockState block = GetBlock(message.BlockId);
        block.Occupancy = message.Occupancy;
        block.Signal = message.Signal;
        block.Switch = message.Switch;
        block.Crossing = message.Crossing;

        OnStateChanged(CtcStateChangeKind.BlockStatus, block.BlockId);
    }

    /// <summary>
    /// Records the speed/authority the Track Controller has authorized for a train,
    /// in SI units exactly as received. A train not yet known to CTC is added to the
    /// dispatched trains, since the wayside is reporting an authorization for it.
    /// </summary>
    public void ApplyTrainAuthorization(TrainAuthorizationStatusMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Guard.NotNullOrWhiteSpace(message.TrainId, nameof(message));

        DispatchedTrainState? train = State.FindDispatchedTrain(message.TrainId);
        if (train is null)
        {
            train = new DispatchedTrainState { TrainId = message.TrainId };
            State.DispatchedTrains.Add(train);
        }

        train.AuthorizedSpeedMetersPerSecond = message.AuthorizedSpeedMetersPerSecond;
        train.AuthorizedAuthorityMeters = message.AuthorizedAuthorityMeters;

        OnStateChanged(CtcStateChangeKind.TrainAuthorization);
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
                await DispatchTrainAsync(entry, cancellationToken);
                OnStateChanged(CtcStateChangeKind.TrainDispatched, trainId: entry.TrainId);
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
    /// Sends the MovementRequest that releases <paramref name="entry"/>'s train, then moves
    /// the train from the dispatch queue to the dispatched trains.
    /// </summary>
    /// <exception cref="Exceptions.MessageSendException">The request could not be delivered; nothing changed.</exception>
    private async Task DispatchTrainAsync(DispatchQueueEntry entry, CancellationToken cancellationToken)
    {
        var scheduledTrain = State.ScheduledTrains.FirstOrDefault(train => train.TrainId == entry.TrainId && train.LineId == entry.LineId)
            ?? throw new InvalidOperationException($"Train '{entry.TrainId}' is queued but no longer scheduled.");

        var request = CreateMovementRequest(scheduledTrain.TrainId);

        await _messageSender.SendAsync(request, cancellationToken);

        // Only now, after a successful send, does the train leave the queue. Removing it first
        // would make CTC believe a train was released when the request never reached the wayside.
        State.DispatchQueue.Remove(entry);

        // The Track Controller may already have reported an authorization for this train;
        // reuse that record rather than creating a duplicate.
        var dispatched = State.FindDispatchedTrain(scheduledTrain.TrainId);
        if (dispatched is null)
        {
            dispatched = new DispatchedTrainState { TrainId = scheduledTrain.TrainId };
            State.DispatchedTrains.Add(dispatched);
        }

        dispatched.LineId = scheduledTrain.LineId;

        // Known authoritatively only at release time: the schedule says the train starts here.
        // StartBlockId was fixed when the schedule was built; it is not re-derived per tick.
        // TODO: real route/yard logic will replace the temporary route-start rule.
        dispatched.CurrentBlockId = scheduledTrain.StartBlockId;
    }

    /// <summary>
    /// Stores a validated schedule and queues its trains for dispatch. Trains already
    /// scheduled on the same line(s) are replaced; other lines' schedules are kept.
    /// Everything is checked before any state changes, so a bad schedule is never partially queued.
    /// </summary>
    /// <exception cref="ArgumentException">A train is blank, duplicated, or on an unknown line.</exception>
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
                throw new ArgumentException($"Train '{train.TrainId}' is scheduled more than once.", nameof(scheduledTrains));
            }

            if (State.FindLine(train.LineId) is null)
            {
                throw new ArgumentException($"Unknown line '{train.LineId}'.", nameof(scheduledTrains));
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
    /// (see <see cref="CloseBlockAsync"/>).
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
        // that needs a future Track Controller -> CTC maintenance status message.
        block.RequestedMaintenanceState = MaintenanceState.Closed;

        OnStateChanged(CtcStateChangeKind.MaintenanceRequest, block.BlockId);
    }

    private void OnStateChanged(CtcStateChangeKind kind, string? blockId = null, string? trainId = null, string? message = null) =>
        StateChanged?.Invoke(this, new CtcStateChangedEventArgs(kind, blockId, trainId, message));

    private CtcBlockState GetBlock(string blockId)
    {
        Guard.NotNullOrWhiteSpace(blockId, nameof(blockId));

        return State.FindBlock(blockId) ?? throw new ArgumentException($"Unknown block '{blockId}'.", nameof(blockId));
    }
}
