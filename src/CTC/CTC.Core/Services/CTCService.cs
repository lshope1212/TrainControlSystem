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
/// No dispatching, routing or authority algorithms yet (see RouteManager / AuthorityManager).
/// </summary>
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

        DispatchedTrainState train = State.FindDispatchedTrain(message.TrainId);
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
    /// Sets CTC's notion of the current time. Intended to be driven by the shared
    /// simulation clock; CTC deliberately has no timer of its own.
    /// </summary>
    public void SetSystemTime(TimeSpan systemTime)
    {
        State.SystemTime = systemTime;

        OnStateChanged(CtcStateChangeKind.SystemTime);
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

        // Nothing has been released yet, so the queue is simply rebuilt from the schedule.
        // TODO: once dispatching exists, preserve entries that have already been released.
        State.DispatchQueue.Clear();
        foreach (var train in State.ScheduledTrains.OrderBy(train => train.DepartureTime))
        {
            State.DispatchQueue.Add(new DispatchQueueEntry
            {
                TrainId = train.TrainId,
                LineId = train.LineId,
                DepartureTime = train.DepartureTime,
            });
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

    private void OnStateChanged(CtcStateChangeKind kind, string? blockId = null) =>
        StateChanged?.Invoke(this, new CtcStateChangedEventArgs(kind, blockId));

    private CtcBlockState GetBlock(string blockId)
    {
        Guard.NotNullOrWhiteSpace(blockId, nameof(blockId));

        return State.FindBlock(blockId) ?? throw new ArgumentException($"Unknown block '{blockId}'.", nameof(blockId));
    }
}
