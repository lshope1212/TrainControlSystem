namespace TrainControl.Contracts.Enums;

/// <summary>Application-level outcome of a request (see <see cref="Messages.RequestAcknowledgementMessage"/>).</summary>
public enum RequestAcknowledgementOutcome
{
    /// <summary>
    /// The request was valid and the receiver took it on. Says nothing about the result: a
    /// movement request accepted for evaluation may still be withheld.
    /// </summary>
    AcceptedForEvaluation = 0,

    /// <summary>The request was refused and has no effect; the acknowledgement explains why.</summary>
    Rejected,

    /// <summary>
    /// A successor movement request (<see cref="Messages.MovementSuggestionMessage.PredecessorRequestId"/>)
    /// was validated and staged behind its predecessor. It is NOT a grant and NOT accepted for
    /// evaluation: it authorizes nothing until the Track Controller observes the train's transition
    /// and promotes it, which CTC learns from the movement report carrying the successor's ID.
    /// </summary>
    PreparedAsSuccessor,
}
