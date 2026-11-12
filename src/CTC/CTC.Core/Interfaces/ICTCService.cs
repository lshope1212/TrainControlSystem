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

    /// <summary>
    /// Brings CTC's territory in line with the Track Model's layout, line by line: an equivalent
    /// repeated layout changes nothing (live status, schedules, queue and train tracking survive),
    /// attribute changes are applied in place, and an incompatible topology change is applied only
    /// to a line with no released trains (otherwise that line keeps its topology and the refusal is
    /// reported). Block IDs are used verbatim and must be unique across all lines.
    /// </summary>
    /// <exception cref="ArgumentException">Blank or duplicate line/block IDs; nothing changed.</exception>
    TrackLayoutSyncResult ApplyTrackLayout(TrackLayoutMessage message);

    /// <summary>
    /// Records an anonymous block status report. When a block becomes occupied that is the next
    /// active-route block of exactly one dispatched train, that train's current block moves
    /// forward (raising <see cref="CtcStateChangeKind.TrainLocationUpdated"/>); the report itself
    /// never identifies a train.
    /// </summary>
    /// <remarks>
    /// A live report (with <see cref="BlockStatusMessage.ReportedAtUtc"/>) older than the one
    /// already applied is ignored, and its movement decision is applied only to the request it names.
    /// </remarks>
    void ApplyBlockStatus(BlockStatusMessage message);

    /// <summary>
    /// Records the Track Controller's answer (accepted for evaluation or rejected) to a CTC
    /// movement, maintenance or switch request. Stale or repeated answers are ignored.
    /// </summary>
    void ApplyRequestAcknowledgement(RequestAcknowledgementMessage message);

    /// <summary>
    /// Records a stage of one of CTC's physical dispatches or route updates, relayed by the Track
    /// Controller. Stale stages are ignored; none of them moves or identifies a train.
    /// </summary>
    void ApplyTrainDispatchStatus(TrainDispatchStatusMessage message);

    /// <summary>
    /// Treats every live block status not refreshed within the freshness window as Unknown (and
    /// unsafe for authority). Call periodically on wall time. Returns how many blocks went stale.
    /// </summary>
    int RefreshBlockStatusFreshness();

    /// <summary>
    /// Sends every due physical dispatch (and resends unsettled ones), then brings every released
    /// train's movement request at the Track Controller in line with its plan and tracked location:
    /// issues, replaces (cancelling the old request first) or cancels requests, and renews still-valid
    /// ones on wall time so the Track Controller's request watchdog keeps them alive. Renewal never
    /// changes a request's limits. Failures are reported, not thrown.
    /// </summary>
    Task SynchronizeMovementRequestsAsync(CancellationToken cancellationToken = default);

    void ApplyTicketSales(TicketSalesMessage message);

    /// <summary>
    /// Records the time reported by the external system clock, then releases every queued
    /// train whose departure time has been reached and whose start block is safe: the train leaves
    /// the queue awaiting placement, its physical dispatch is sent to the Track Controller, and its
    /// first movement request is issued once its start block reports occupied. Also starts the next
    /// movement of trains whose dwell ended.
    /// Dispatch failures are reported through <see cref="StateChanged"/>, not thrown.
    /// </summary>
    Task SetSystemTimeAsync(TimeSpan systemTime, CancellationToken cancellationToken = default);

    // Scheduling.

    /// <summary>
    /// Replaces the schedule for every line in <paramref name="scheduledTrains"/> and
    /// rebuilds the pending dispatch queue from it, ordered by departure time. Trains that
    /// have already been dispatched are never queued again. Does not dispatch by itself.
    /// A schedule that needs more than the permitted speed on any segment is rejected, as is
    /// one with a train ID outside the supported fleet ("001" to "010") or an ID another line
    /// already uses: train IDs are unique across the whole system.
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

    /// <summary>
    /// Requests that the Track Controller reopen <paramref name="blockId"/> after maintenance.
    /// Records only the request; the block's confirmed maintenance state changes when the
    /// Track Controller reports it via <see cref="ApplyBlockStatus"/>.
    /// </summary>
    Task ReopenBlockAsync(string blockId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests that the Track Controller move the switch in <paramref name="blockId"/> to
    /// <paramref name="requestedPosition"/>. Does not change the block's switch state; that
    /// changes only when the Track Controller reports it via <see cref="ApplyBlockStatus"/>.
    /// </summary>
    Task SetSwitchPositionAsync(string blockId, SwitchPosition requestedPosition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes an already-dispatched train's destination and/or arrival time: CTC routes it
    /// from its current (occupancy-inferred) block to <paramref name="destinationBlockId"/>, recalculates its
    /// suggested speed and authority, and (for a train on the track) replaces its movement request.
    /// The train's state changes only if any needed send succeeded.
    /// </summary>
    /// <returns>A dispatcher-readable warning (e.g. the arrival time cannot be met), or null.</returns>
    /// <exception cref="Exceptions.DispatchException">The plan cannot be applied (dispatcher-readable reason); nothing was sent.</exception>
    /// <exception cref="Exceptions.MessageSendException">The suggestion could not be delivered; nothing changed.</exception>
    Task<string?> UpdateDispatchedTrainPlanAsync(string trainId, string destinationBlockId, TimeSpan targetArrivalTime, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes one stop (<paramref name="stopIndex"/> 0 = current stop) of an already-dispatched
    /// train's Active Movement Plan: its destination, arrival time and dwell time. CTC validates
    /// the timing (each stop entered after the previous stop's arrival + dwell), recalculates the
    /// dependent suggested speeds, and replaces the movement request only when the movement under
    /// way or the route changed. A dwell change alone never changes the route or authority.
    /// </summary>
    /// <returns>A dispatcher-readable warning (e.g. an arrival time cannot be met), or null.</returns>
    /// <exception cref="Exceptions.DispatchException">The change cannot be applied (dispatcher-readable reason); nothing was sent or changed.</exception>
    /// <exception cref="Exceptions.MessageSendException">The suggestion could not be delivered; nothing changed.</exception>
    Task<string?> UpdateMovementPlanStopAsync(string trainId, int stopIndex, string destinationBlockId, TimeSpan arrivalTime, TimeSpan dwellTime, CancellationToken cancellationToken = default);
}
