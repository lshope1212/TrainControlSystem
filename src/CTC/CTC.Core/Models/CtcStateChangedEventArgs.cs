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
