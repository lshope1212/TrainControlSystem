namespace TrainControl.Contracts.Enums;

/// <summary>
/// Kind of movement request CTC can send to the Track Controller.
/// Switch changes are NOT movement requests; they use SwitchPositionRequestMessage.
/// </summary>
public enum MovementRequestType
{
    ReleaseTrain = 0
}
