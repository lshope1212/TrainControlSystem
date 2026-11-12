using TrainControl.Contracts.Enums;

namespace CTC.Core.Models;

/// <summary>
/// CTC's view of a single block: static layout data from the Track Model plus the
/// latest wayside status reported by the Track Controller.
/// </summary>
public class CtcBlockState
{
    // Static layout (Track Model).

    public string BlockId { get; set; } = string.Empty;

    public int BlockNumber { get; set; }

    public string Section { get; set; } = string.Empty;

    public double LengthMeters { get; set; }

    /// <summary>
    /// Civil speed limit of the block, in km/h exactly as received from the Track Model.
    /// A property of the track, not of any train. Zero means the layout gave no limit.
    /// </summary>
    public double SpeedLimitKilometersPerHour { get; set; }

    /// <summary>Empty when the block has no station.</summary>
    public string StationName { get; set; } = string.Empty;

    public bool HasSwitch { get; set; }

    public bool HasSignal { get; set; }

    public bool HasCrossing { get; set; }

    public IList<string> ConnectedBlockIds { get; } = new List<string>();

    // Live wayside status (Track Controller).

    public OccupancyState Occupancy { get; set; } = OccupancyState.Unknown;

    /// <summary>
    /// The last DEFINITE occupancy (Clear or Occupied) reported for the block; null before any. A block
    /// that went Unknown (stale reports, a track circuit failure) and comes back Occupied, having last
    /// been Occupied, shows no new train entering it, so train tracking does not treat it as an entry.
    /// </summary>
    public OccupancyState? LastDefiniteOccupancy { get; set; }

    public SignalState Signal { get; set; } = SignalState.Unknown;

    public SwitchPosition Switch { get; set; } = SwitchPosition.Unknown;

    public CrossingState Crossing { get; set; } = CrossingState.Unknown;

    /// <summary>
    /// The maintenance state the Track Controller most recently REPORTED for this block
    /// (via BlockStatusMessage): the block's actual wayside state.
    /// </summary>
    public MaintenanceState ConfirmedMaintenanceState { get; set; } = MaintenanceState.Open;

    // CTC's own requests.

    /// <summary>
    /// The maintenance state CTC has most recently REQUESTED (and successfully sent)
    /// for this block. Closed means "CTC issued a Close request", NOT "the Track
    /// Controller confirmed the block is closed" (see <see cref="ConfirmedMaintenanceState"/>).
    /// While the two differ, a request is awaiting Track Controller confirmation.
    /// </summary>
    public MaintenanceState RequestedMaintenanceState { get; set; } = MaintenanceState.Open;

    /// <summary>
    /// ID of CTC's latest maintenance request for this block that the Track Controller has not
    /// acknowledged yet; empty when none is outstanding. Acknowledgements for any other ID are stale.
    /// </summary>
    public string PendingMaintenanceRequestId { get; set; } = string.Empty;

    /// <summary>ID of CTC's latest unacknowledged switch request for this block; empty when none.</summary>
    public string PendingSwitchRequestId { get; set; } = string.Empty;

    // Freshness and ordering of the live wayside status.

    /// <summary>
    /// Wayside evaluation time of the status currently applied, when its sender reports
    /// periodically (<c>BlockStatusMessage.ReportedAtUtc</c>). A later message with an older time
    /// is a late duplicate and is ignored. Null for a status from a non-periodic sender (e.g. the
    /// CTC TestUI).
    /// </summary>
    public DateTimeOffset? StatusReportedAtUtc { get; set; }

    /// <summary>
    /// Wall-clock time CTC received the current periodic (live) status; null when the status is not
    /// live. A live status that is not refreshed in time becomes stale.
    /// </summary>
    public DateTimeOffset? LiveStatusReceivedAtUtc { get; set; }

    /// <summary>
    /// True when live wayside reports for this block stopped arriving: CTC no longer knows its
    /// state, so occupancy and equipment read Unknown (and the block is unsafe for authority)
    /// until a new report arrives. Maintenance keeps its last confirmed value.
    /// </summary>
    public bool IsStatusStale { get; set; }
}
