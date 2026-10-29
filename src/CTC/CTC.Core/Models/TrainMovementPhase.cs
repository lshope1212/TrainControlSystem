namespace CTC.Core.Models;

/// <summary>
/// Where a dispatched train is in its Active Movement Plan, relative to
/// <see cref="DispatchedTrainState.CurrentStop"/>: <c>EnRoute → Arrived/Dwelling → EnRoute (next stop) … → Completed</c>.
/// </summary>
/// <remarks>
/// Driven only by what CTC already receives: occupancy (the train entering its stop block,
/// see <see cref="Tracking.TrainLocationTracker"/>) and SystemTime updates (the dwell elapsing).
/// CTC has no clock of its own.
/// </remarks>
public enum TrainMovementPhase
{
    /// <summary>Travelling toward the current stop.</summary>
    EnRoute = 0,

    /// <summary>
    /// In the current stop block, with its dwell finished (or zero), ready to proceed: the next
    /// movement starts on the next SystemTime update (or is retried there if sending failed).
    /// </summary>
    Arrived,

    /// <summary>In the current stop block, waiting for its dwell time to elapse.</summary>
    Dwelling,

    /// <summary>At the final stop with its dwell finished; the plan has no further movement.</summary>
    Completed,
}
