using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;

namespace TrainControl.Contracts.Messages;

/// <summary>
/// The Track Controller's application-level answer to one CTC request
/// (<see cref="MovementSuggestionMessage"/>, <see cref="MaintenanceRequestMessage"/> or
/// <see cref="SwitchPositionRequestMessage"/>), correlated by <see cref="RequestId"/>.
/// Direction: Track Controller -> CTC.
/// </summary>
/// <remarks>
/// <para>
/// Four separate facts must never be confused:
/// transport delivery (the sender's send completed: bytes reached the pipe),
/// <see cref="RequestAcknowledgementOutcome.AcceptedForEvaluation"/> (the request was valid and the
/// wayside took it on), <see cref="RequestAcknowledgementOutcome.Rejected"/> (it was refused; see
/// <see cref="Detail"/>), and the actual result, e.g. a movement authorization
/// (<see cref="MovementAuthorizationReport"/>) or a confirmed block closure
/// (<see cref="BlockStatusMessage.Maintenance"/>). Accepted for evaluation is NOT a granted authority.
/// </para>
/// <para>
/// A receiver ignores an acknowledgement whose <see cref="RequestId"/> is not the request it is
/// still waiting on (stale or duplicate).
/// </para>
/// </remarks>
public sealed class RequestAcknowledgementMessage
{
    /// <summary>The request's ID; empty when the request carried none (legacy sender) or could not be read.</summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary>The acknowledged request's contract name, e.g. "MovementSuggestionMessage".</summary>
    public string RequestType { get; set; } = string.Empty;

    public RequestAcknowledgementOutcome Outcome { get; set; }

    /// <summary>True when the acknowledged movement request already ended at the wayside.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsRetiredMovement { get; set; }

    /// <summary>Readable explanation from the wayside, e.g. the rejection reason.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>The request's (target) block, when it names one.</summary>
    public string BlockId { get; set; } = string.Empty;

    /// <summary>The request's train label, when it carries one.</summary>
    public string TrainId { get; set; } = string.Empty;

    /// <summary>Wall-clock time (UTC) the wayside decided.</summary>
    public DateTimeOffset AcknowledgedAtUtc { get; set; }
}
