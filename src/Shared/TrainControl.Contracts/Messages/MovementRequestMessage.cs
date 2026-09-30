using TrainControl.Contracts.Enums;

namespace TrainControl.Contracts.Messages;

/// <summary>
/// Request to release/dispatch a train for movement.
/// Direction: CTC -> Track Controller.
/// </summary>
/// <remarks>
/// Switch changes are sent with <see cref="SwitchPositionRequestMessage"/>, not here.
/// </remarks>
public class MovementRequestMessage
{
    public string TrainId { get; set; } = string.Empty;

    public MovementRequestType RequestType { get; set; } = MovementRequestType.ReleaseTrain;
}
