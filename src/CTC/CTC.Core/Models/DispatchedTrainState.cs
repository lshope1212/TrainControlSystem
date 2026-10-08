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
    /// Last block CTC can authoritatively associate with the train; empty when unknown.
    /// Set to the schedule's route start block when CTC releases the train, because that is
    /// where the schedule says it begins. It is NOT necessarily where the train is now: Track
    /// Controller occupancy alone cannot update it, because <c>BlockStatusMessage</c> carries no
    /// TrainId, so CTC cannot tell which train moved into a block and must not guess.
    /// TODO: update it from a train-position message (train identity + location) once the
    /// modules are integrated.
    /// </summary>
    public string LastKnownBlockId { get; set; } = string.Empty;
}
