namespace CTC.Core.Models;

/// <summary>
/// CTC's view of a train it has released onto the line.
/// </summary>
/// <remarks>
/// The train's original <see cref="ScheduledTrain"/> is kept unchanged in
/// <see cref="CtcSystemState.ScheduledTrains"/>; this records the plan CTC is CURRENTLY
/// suggesting, which the dispatcher can change after release.
/// </remarks>
public class DispatchedTrainState
{
    /// <summary>Three-digit train ID assigned when the schedule was built, e.g. "000" (see <see cref="TrainIds"/>).</summary>
    public string TrainId { get; set; } = string.Empty;

    /// <summary>Line the train was dispatched on.</summary>
    public string LineId { get; set; } = string.Empty;

    /// <summary>
    /// Speed in the latest MovementSuggestion CTC successfully sent for this train (NOT what
    /// the Track Controller authorized). Set at release and whenever the dispatcher changes the
    /// movement plan; it is not recalculated automatically as the train moves.
    /// </summary>
    public double SuggestedSpeedMetersPerSecond { get; set; }

    /// <summary>
    /// Authority in the latest MovementSuggestion CTC successfully sent for this train (NOT
    /// what the Track Controller authorized). Set at release and whenever the dispatcher
    /// changes the movement plan; it is not recalculated automatically as the train moves.
    /// </summary>
    public double SuggestedAuthorityMeters { get; set; }

    /// <summary>
    /// Block CTC currently places the train in: the foremost block it has logically advanced
    /// the train into; empty when unknown. Set to the schedule's route start block at release,
    /// then inferred from sequential track occupancy by <see cref="Tracking.TrainLocationTracker"/>:
    /// when the next block of <see cref="ActiveRouteBlockIds"/> becomes occupied and no other
    /// train could explain it, the train moves into that block. <c>BlockStatusMessage</c> never
    /// names the train; the identity comes only from CTC's own dispatch and route knowledge.
    /// A trailing block clearing never moves it. Re-planning (route, speed and authority)
    /// starts from this block.
    /// </summary>
    public string CurrentBlockId { get; set; } = string.Empty;

    /// <summary>
    /// The blocks CTC currently expects this train to follow, in travel order. At release, the
    /// scheduled route; after the dispatcher changes the plan, the newly calculated route
    /// (starting at <see cref="CurrentBlockId"/>). Runtime state only: the train's
    /// <see cref="ScheduledTrain.Route"/> is never changed.
    /// </summary>
    public IList<string> ActiveRouteBlockIds { get; } = new List<string>();

    /// <summary>Position of <see cref="CurrentBlockId"/> in <see cref="ActiveRouteBlockIds"/>.</summary>
    public int ActiveRouteIndex { get; set; }

    /// <summary>
    /// True once the Track Controller has reported <see cref="CurrentBlockId"/> occupied. The
    /// train is advanced to its next route block only after that, so CTC never carries a train
    /// identity forward from a block it has not seen the train in. The block clearing later does
    /// not reset it, so the old block may clear before the next one is reported occupied.
    /// </summary>
    public bool CurrentBlockOccupancyObserved { get; set; }

    /// <summary>
    /// Block the latest suggested speed is planned to reach: at release, the next timed block
    /// of the schedule; afterwards, the destination the dispatcher chose. Not necessarily the
    /// schedule's final block.
    /// </summary>
    public string TargetBlockId { get; set; } = string.Empty;

    /// <summary>Time of simulation day the train is planned to enter <see cref="TargetBlockId"/>.</summary>
    public TimeSpan TargetArrivalTime { get; set; }
}
