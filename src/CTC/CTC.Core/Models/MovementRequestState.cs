using CTC.Core.Dispatching;
using TrainControl.Contracts.Enums;

namespace CTC.Core.Models;

/// <summary>Transport outcome of CTC's latest send of a movement request.</summary>
public enum MovementRequestDelivery
{
    NotSent = 0,

    /// <summary>The bytes reached the Track Controller's pipe. Says nothing about acceptance.</summary>
    Delivered,

    /// <summary>The last send failed; CTC retries it with the same request ID.</summary>
    Failed,
}

/// <summary>
/// One movement request CTC holds at the Track Controller for a train, and everything CTC has
/// learned about it, kept apart: transport delivery, the wayside's acknowledgement (accepted for
/// evaluation or rejected), and the wayside's actual authorization.
/// </summary>
/// <remarks>
/// The request ID identifies exactly one <see cref="Plan"/> (target, entry, path, speed and
/// authority). Renewing it resends the same ID and plan; any change to the plan gets a new ID and
/// the old request is cancelled first.
/// </remarks>
public sealed class MovementRequestState
{
    public MovementRequestState(string requestId, MovementRequestPlan plan)
    {
        RequestId = string.IsNullOrWhiteSpace(requestId) ? throw new ArgumentException("A request ID is required.", nameof(requestId)) : requestId;
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
    }

    public string RequestId { get; }

    public MovementRequestPlan Plan { get; }

    public MovementRequestDelivery Delivery { get; set; }

    /// <summary>True once any send was attempted, so the Track Controller may hold it and it must be cancelled before replacement.</summary>
    public bool HasBeenAttempted { get; set; }

    /// <summary>Wall-clock time of the latest send attempt, successful or not.</summary>
    public DateTimeOffset? LastAttemptAtUtc { get; set; }

    /// <summary>Wall-clock time of the latest successful delivery (initial send or renewal).</summary>
    public DateTimeOffset? LastDeliveredAtUtc { get; set; }

    /// <summary>Why the latest send failed; empty otherwise.</summary>
    public string DeliveryError { get; set; } = string.Empty;

    /// <summary>The Track Controller's answer; null until it answers.</summary>
    public RequestAcknowledgementOutcome? Acknowledgement { get; set; }

    public string AcknowledgementDetail { get; set; } = string.Empty;

    /// <summary>What the Track Controller actually commands for this request.</summary>
    public MovementAuthorizationState Authorization { get; set; } = MovementAuthorizationState.None;

    public double AuthorizedSpeedMetersPerSecond { get; set; }

    public double AuthorizedAuthorityMeters { get; set; }

    public string AuthorizationReason { get; set; } = string.Empty;

    /// <summary>True only when the wayside reports it actually grants movement.</summary>
    public bool IsMovementAuthorized => Authorization is MovementAuthorizationState.Approved or MovementAuthorizationState.Restricted;
}
