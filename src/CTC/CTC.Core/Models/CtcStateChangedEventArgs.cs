namespace CTC.Core.Models;

/// <summary>Which part of <see cref="CtcSystemState"/> a change affected.</summary>
public enum CtcStateChangeKind
{
    /// <summary>
    /// Lines were added, removed or rebuilt, or static block attributes changed in place; rebuild
    /// any cached line/block references. Not raised for an equivalent repeated layout.
    /// <see cref="CtcStateChangedEventArgs.Message"/> summarizes what changed.
    /// </summary>
    TrackLayout,

    /// <summary>
    /// Part of a received layout was refused (an incompatible topology change on a line with
    /// released trains); that line kept its topology. <see cref="CtcStateChangedEventArgs.Message"/> says why.
    /// </summary>
    TrackLayoutRejected,

    BlockStatus,

    TicketSales,

    SystemTime,

    MaintenanceRequest,

    /// <summary>The scheduled trains were replaced.</summary>
    Schedule,

    /// <summary>The dispatch queue was rebuilt.</summary>
    DispatchQueue,

    /// <summary>
    /// A train was released: its departure time came and its start block was safe, so it left the
    /// dispatch queue and was added to the dispatched trains, awaiting placement on the track. This is
    /// CTC's decision only: no module confirms a physical release yet, and no message is sent until
    /// the train is detected at its start block (<see cref="TrainDetectedOnTrack"/>).
    /// <see cref="CtcStateChangedEventArgs.TrainId"/> names the train;
    /// <see cref="CtcStateChangedEventArgs.Message"/> carries a warning (e.g. the train left late) or is null.
    /// </summary>
    TrainDispatched,

    /// <summary>
    /// A released train's start block became occupied and CTC attributes it to that train (inferred
    /// from occupancy); its movement requests start now. <see cref="CtcStateChangedEventArgs.BlockId"/>
    /// is the block.
    /// </summary>
    TrainDetectedOnTrack,

    /// <summary>
    /// A train's movement request changed: issued, cancelled, a send failed, or the Track Controller
    /// acknowledged it or changed its authorization. <see cref="CtcStateChangedEventArgs.Message"/>
    /// describes it.
    /// </summary>
    MovementRequestUpdated,

    /// <summary>
    /// The Track Controller answered a maintenance or switch request (accepted for evaluation or
    /// rejected). A rejected maintenance request is withdrawn: its requested state reverts to the
    /// confirmed state. <see cref="CtcStateChangedEventArgs.Message"/> gives the wayside's detail.
    /// </summary>
    RequestAcknowledged,

    /// <summary>
    /// Live wayside reports for one or more blocks stopped arriving; their status is now Unknown.
    /// <see cref="CtcStateChangedEventArgs.Message"/> names them.
    /// </summary>
    BlockStatusStale,

    /// <summary>
    /// A train could not be released (e.g. its start block is occupied, of unknown occupancy or
    /// closed). Nothing was dispatched and the train stays queued for retry;
    /// <see cref="CtcStateChangedEventArgs.Message"/> gives the reason.
    /// </summary>
    DispatchFailed,

    /// <summary>
    /// The dispatcher changed a stop of a released train's Active Movement Plan (destination,
    /// arrival time and/or dwell time) and CTC recalculated the dependent timing; a new
    /// MovementSuggestion was sent if the current movement or the route changed.
    /// <see cref="CtcStateChangedEventArgs.TrainId"/> names the train;
    /// <see cref="CtcStateChangedEventArgs.Message"/> carries a warning (e.g. the arrival time
    /// cannot be met) or is null.
    /// </summary>
    TrainMovementPlanUpdated,

    /// <summary>
    /// A train's <see cref="TrainMovementPhase"/> changed: it arrived at a stop, started or
    /// finished dwelling, departed toward its next stop (its new MovementSuggestion was sent),
    /// or completed its plan. <see cref="CtcStateChangedEventArgs.TrainId"/> names the train;
    /// <see cref="CtcStateChangedEventArgs.Message"/> describes the change.
    /// </summary>
    TrainMovementPhaseChanged,

    /// <summary>
    /// The MovementSuggestion for a train's next movement (after dwelling) could not be sent.
    /// The train keeps its plan and the send is retried on the next SystemTime update;
    /// <see cref="CtcStateChangedEventArgs.Message"/> gives the reason.
    /// </summary>
    TrainMovementSuggestionFailed,

    /// <summary>
    /// A block became occupied that is the next active-route block of exactly one dispatched
    /// train, so CTC moved that train's current block forward. Raised after the block's own
    /// <see cref="BlockStatus"/> change. <see cref="CtcStateChangedEventArgs.TrainId"/> names the
    /// train and <see cref="CtcStateChangedEventArgs.BlockId"/> its new current block.
    /// </summary>
    TrainLocationUpdated,

    /// <summary>
    /// A block became occupied that could be the next block of more than one dispatched train.
    /// CTC does not guess: no train was moved. <see cref="CtcStateChangedEventArgs.BlockId"/> is
    /// the block; <see cref="CtcStateChangedEventArgs.Message"/> names the candidate trains.
    /// </summary>
    TrainLocationAmbiguous,
}

/// <summary>
/// Raised by <see cref="Services.CTCService"/> after it changes <see cref="CtcSystemState"/>.
/// Plain .NET event data so CTC.Core stays UI independent.
/// </summary>
public sealed class CtcStateChangedEventArgs : EventArgs
{
    public CtcStateChangedEventArgs(CtcStateChangeKind kind, string? blockId = null, string? trainId = null, string? message = null)
    {
        Kind = kind;
        BlockId = blockId;
        TrainId = trainId;
        Message = message;
    }

    public CtcStateChangeKind Kind { get; }

    /// <summary>The affected block, for block-specific changes; otherwise null.</summary>
    public string? BlockId { get; }

    /// <summary>The affected train, for train-specific changes; otherwise null.</summary>
    public string? TrainId { get; }

    /// <summary>Dispatcher-readable detail, e.g. why a dispatch failed; otherwise null.</summary>
    public string? Message { get; }
}
