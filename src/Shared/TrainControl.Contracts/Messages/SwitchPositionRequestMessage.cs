using TrainControl.Contracts.Enums;

namespace TrainControl.Contracts.Messages;

/// <summary>
/// Request to move the switch in a block to a given position.
/// Direction: CTC -> Track Controller.
/// </summary>
/// <remarks>
/// The Track Controller answers with a <see cref="RequestAcknowledgementMessage"/> carrying the
/// same <see cref="RequestId"/>; the switch's actual position is reported only in
/// <see cref="BlockStatusMessage.Switch"/>.
/// </remarks>
public class SwitchPositionRequestMessage
{
    /// <summary>Correlates the Track Controller's acknowledgement; empty for legacy senders.</summary>
    public string RequestId { get; set; } = string.Empty;

    public string BlockId { get; set; } = string.Empty;

    public SwitchPosition RequestedPosition { get; set; } = SwitchPosition.Unknown;
}
