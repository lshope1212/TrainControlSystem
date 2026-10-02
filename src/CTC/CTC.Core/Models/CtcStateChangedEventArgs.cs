namespace CTC.Core.Models;

/// <summary>Which part of <see cref="CtcSystemState"/> a change affected.</summary>
public enum CtcStateChangeKind
{
    /// <summary>Lines and blocks were replaced; any cached block references are stale.</summary>
    TrackLayout,

    BlockStatus,

    TrainAuthorization,

    TicketSales,

    SystemTime,

    MaintenanceRequest,
}

/// <summary>
/// Raised by <see cref="Services.CTCService"/> after it changes <see cref="CtcSystemState"/>.
/// Plain .NET event data so CTC.Core stays UI independent.
/// </summary>
public sealed class CtcStateChangedEventArgs : EventArgs
{
    public CtcStateChangedEventArgs(CtcStateChangeKind kind, string? blockId = null)
    {
        Kind = kind;
        BlockId = blockId;
    }

    public CtcStateChangeKind Kind { get; }

    /// <summary>The affected block, for block-specific changes; otherwise null.</summary>
    public string? BlockId { get; }
}
