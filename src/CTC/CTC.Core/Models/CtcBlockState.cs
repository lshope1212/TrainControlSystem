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
}
