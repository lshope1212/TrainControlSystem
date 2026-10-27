namespace TrainControl.Contracts.Messages;

/// <summary>
/// The Train Controller's ruling on a pending passenger emergency-brake request.
/// Approve engages the emergency brake; Deny clears the pending request without
/// engaging. Only the controller may send this — never the passenger.
/// </summary>
public class EmergencyBrakeApprovalMessage
{
    public string TrainId { get; set; } = string.Empty;

    /// <summary>True = approve (engage brake); false = deny (clear pending, no engage).</summary>
    public bool Approved { get; set; }
}
