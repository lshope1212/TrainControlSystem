using TrainControl.Contracts.Enums;

namespace TrainControl.Contracts.Messages;

/// <summary>
/// Wayside status of a single block.
/// Direction: Track Controller -> CTC.
/// </summary>
/// <remarks>
/// <para>
/// Occupancy does not identify which train occupies the block. Every equipment value is the
/// MEASURED/confirmed state the wayside has, never the state it commanded or was asked for.
/// </para>
/// <para>
/// <see cref="BlockId"/> is the canonical block identifier from the Track Model layout (see
/// <see cref="TrackLayoutMessage"/>); receivers use it verbatim and never parse it.
/// </para>
/// </remarks>
public class BlockStatusMessage
{
    public string BlockId { get; set; } = string.Empty;

    public OccupancyState Occupancy { get; set; } = OccupancyState.Unknown;

    public SignalState Signal { get; set; } = SignalState.Unknown;

    public SwitchPosition Switch { get; set; } = SwitchPosition.Unknown;

    public CrossingState Crossing { get; set; } = CrossingState.Unknown;

    /// <summary>Whether the Track Controller actually has the block open or closed for maintenance.</summary>
    public MaintenanceState Maintenance { get; set; } = MaintenanceState.Open;

    /// <summary>
    /// Wall-clock time (UTC) at which the wayside evaluated this status. A live wayside sets it on
    /// every report and keeps reporting periodically, so a receiver can ignore a late report that
    /// is older than one it already applied and can treat a block whose reports stop as stale.
    /// Null for senders that do not report periodically (e.g. a TestUI): such a status is applied
    /// in arrival order and is held until replaced.
    /// </summary>
    public DateTimeOffset? ReportedAtUtc { get; set; }

    /// <summary>
    /// The wayside's decision on the movement request whose target is this block, or null when no
    /// movement request targets it. Correlated by <see cref="MovementAuthorizationReport.RequestId"/>.
    /// </summary>
    public MovementAuthorizationReport? Movement { get; set; }
}
