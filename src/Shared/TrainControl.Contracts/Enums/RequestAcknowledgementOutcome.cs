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
}
