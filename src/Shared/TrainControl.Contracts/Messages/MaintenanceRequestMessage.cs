using TrainControl.Contracts.Enums;

namespace TrainControl.Contracts.Messages;

/// <summary>
/// Request to open or close a block for maintenance.
/// Direction: CTC -> Track Controller.
/// </summary>
/// <remarks>
/// The Track Controller answers with a <see cref="RequestAcknowledgementMessage"/> carrying the
/// same <see cref="RequestId"/>; the block's actual state is reported only in
/// <see cref="BlockStatusMessage.Maintenance"/>.
/// </remarks>
public class MaintenanceRequestMessage
{
    /// <summary>Correlates the Track Controller's acknowledgement; empty for legacy senders.</summary>
    public string RequestId { get; set; } = string.Empty;

    public string BlockId { get; set; } = string.Empty;

    public MaintenanceState RequestedState { get; set; } = MaintenanceState.Open;
}
