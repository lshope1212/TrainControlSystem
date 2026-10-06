using CTC.Core.Models;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace CTC.Core.Interfaces;

/// <summary>
/// Boundary between shared contract messages and CTC's own domain state.
/// Outgoing messages are transmitted only through an injected <see cref="IMessageSender"/>.
/// </summary>
public interface ICTCService
{
    CtcSystemState State { get; }

    /// <summary>
    /// Raised synchronously, on the calling thread, after any method changes <see cref="State"/>.
    /// Lets presentation layers refresh without the domain models knowing about UI.
    /// </summary>
    event EventHandler<CtcStateChangedEventArgs>? StateChanged;

    // Inbound: map shared contracts into CTC state.

    void ApplyTrackLayout(TrackLayoutMessage message);

    void ApplyBlockStatus(BlockStatusMessage message);

    void ApplyTrainAuthorization(TrainAuthorizationStatusMessage message);

    void ApplyTicketSales(TicketSalesMessage message);

    void SetSystemTime(TimeSpan systemTime);

    // Scheduling.

    /// <summary>
    /// Replaces the schedule for every line in <paramref name="scheduledTrains"/> and
    /// rebuilds the dispatch queue from it, ordered by departure time. Does not dispatch.
    /// </summary>
    void QueueSchedule(IEnumerable<ScheduledTrain> scheduledTrains);

    // Outbound: build shared contracts for dispatcher actions (not sent).

    MaintenanceRequestMessage CreateMaintenanceRequest(string blockId, MaintenanceState requestedState);

    SwitchPositionRequestMessage CreateSwitchPositionRequest(string blockId, SwitchPosition requestedPosition);

    MovementRequestMessage CreateMovementRequest(string trainId);

    // Dispatcher actions that are actually sent to the Track Controller.

    /// <summary>
    /// Requests that the Track Controller close <paramref name="blockId"/> for maintenance.
    /// </summary>
    Task CloseBlockAsync(string blockId, CancellationToken cancellationToken = default);
}
