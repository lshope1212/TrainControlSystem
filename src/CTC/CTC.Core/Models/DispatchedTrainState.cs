namespace CTC.Core.Models;

/// <summary>
/// CTC's view of a train it has released onto the line.
/// </summary>
public class DispatchedTrainState
{
    // TODO: TrainId is still the schedule's placeholder ("Train 1", ...). Mapping it to the
    // real Train Model train ID needs the integrated system, so the same placeholder on two
    // lines would currently refer to one DispatchedTrainState.
    public string TrainId { get; set; } = string.Empty;

    /// <summary>Line the train was dispatched on.</summary>
    public string LineId { get; set; } = string.Empty;

    /// <summary>
    /// Speed CTC suggested to the Track Controller when it released the train (NOT what the
    /// Track Controller authorized). Initial value only: it is not recalculated as the train moves.
    /// </summary>
    public double SuggestedSpeedMetersPerSecond { get; set; }

    /// <summary>
    /// Authority CTC suggested to the Track Controller when it released the train (NOT what
    /// the Track Controller authorized). Initial value only: it is not recalculated as the train moves.
    /// </summary>
    public double SuggestedAuthorityMeters { get; set; }

    /// <summary>
    /// Block the train currently occupies; empty when unknown. Set to the schedule's route
    /// start block when CTC releases the train, because that is where the schedule says it begins.
    /// KNOWN GAP: it is NOT updated after that. Block occupancy from the Track Controller
    /// carries no train ID, so CTC cannot tell which train moved into a block and must not guess.
    /// </summary>
    public string CurrentBlockId { get; set; } = string.Empty;
}
