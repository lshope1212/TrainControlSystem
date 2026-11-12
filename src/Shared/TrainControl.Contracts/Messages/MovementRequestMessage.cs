using TrainControl.Contracts.Enums;

namespace TrainControl.Contracts.Messages;

/// <summary>
/// Legacy release intent (a train label only). Superseded by <see cref="TrainDispatchRequestMessage"/>
/// and not sent by any module.
/// </summary>
/// <remarks>
/// <para>
/// Neither a placement nor a movement authorization. A train label alone identifies no block and no
/// route, so the Track Controller rejects this message. Physical dispatch uses
/// <see cref="TrainDispatchRequestMessage"/> (train, line, start block and ordered route), which the
/// Track Controller forwards to the Track Model; movement is authorized only from a complete
/// <see cref="MovementSuggestionMessage"/>.
/// </para>
/// <para>Kept so existing serialized data and tests still read. Switch changes use <see cref="SwitchPositionRequestMessage"/>.</para>
/// </remarks>
public class MovementRequestMessage
{
    public string TrainId { get; set; } = string.Empty;

    public MovementRequestType RequestType { get; set; } = MovementRequestType.ReleaseTrain;
}
