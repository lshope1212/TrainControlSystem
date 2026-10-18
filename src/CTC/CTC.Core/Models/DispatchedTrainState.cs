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
    /// Last block CTC can authoritatively associate with the train; empty when unknown.
    /// Set to the schedule's route start block when CTC releases the train, because that is
    /// where the schedule says it begins. It is NOT necessarily where the train is now: Track
    /// Controller occupancy alone cannot update it, because <c>BlockStatusMessage</c> carries no
    /// TrainId, so CTC cannot tell which train moved into a block and must not guess.
    /// Re-planning (route, speed and authority) always starts from this block, so once it is
    /// kept up to date the re-planning logic needs no change.
    /// TODO: update it from a train-position message (train identity + location) once the
    /// modules are integrated.
    /// </summary>
    public string LastKnownBlockId { get; set; } = string.Empty;

    /// <summary>
    /// Block the latest suggested speed is planned to reach: at release, the next timed block
    /// of the schedule; afterwards, the destination the dispatcher chose. Not necessarily the
    /// schedule's final block.
    /// </summary>
    public string TargetBlockId { get; set; } = string.Empty;

    /// <summary>Time of simulation day the train is planned to enter <see cref="TargetBlockId"/>.</summary>
    public TimeSpan TargetArrivalTime { get; set; }
}
