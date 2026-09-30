using TrainControl.Contracts.Enums;

namespace TrainControl.Contracts.Messages;

/// <summary>
/// Request to move the switch in a block to a given position.
/// Direction: CTC -> Track Controller.
/// </summary>
public class SwitchPositionRequestMessage
{
    public string BlockId { get; set; } = string.Empty;

    public SwitchPosition RequestedPosition { get; set; } = SwitchPosition.Unknown;
}
