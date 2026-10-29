namespace CTC.Core.Models;

/// <summary>Which part of <see cref="CtcSystemState"/> a change affected.</summary>
public enum CtcStateChangeKind
{
    /// <summary>Lines and blocks were replaced; any cached block references are stale.</summary>
    TrackLayout,

    BlockStatus,

    TicketSales,

    SystemTime,

    MaintenanceRequest,

    /// <summary>The scheduled trains were replaced.</summary>
    Schedule,

    /// <summary>The dispatch queue was rebuilt.</summary>
    DispatchQueue,

    /// <summary>
    /// A train's MovementSuggestion and MovementRequest were sent: it left the dispatch queue
    /// and was added to the dispatched trains. <see cref="CtcStateChangedEventArgs.TrainId"/>
    /// names the train; <see cref="CtcStateChangedEventArgs.Message"/> carries a warning
    /// (e.g. the train left late) or is null.
    /// </summary>
    TrainDispatched,

    /// <summary>
    /// A train could not be released (a send failed, or its start block is unsafe). Nothing
    /// was dispatched and the train stays queued for retry;
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
