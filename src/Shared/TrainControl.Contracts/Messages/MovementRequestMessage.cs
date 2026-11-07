using TrainControl.Contracts.Enums;

namespace TrainControl.Contracts.Messages;

/// <summary>
/// Release intent: CTC wants a train placed in service for its scheduled movement.
/// </summary>
/// <remarks>
/// <para>
/// Not a movement authorization and not sent to the Track Controller: the Track Controller
/// authorizes movement only from a complete <see cref="MovementSuggestionMessage"/> (target
/// block, route and authority) and rejects this message, because a train label alone cannot
/// identify a block. Placing a train in service and producing its physical occupancy is the
/// Train Model / Track Model integration's job.
/// </para>
/// <para>
/// No module receives this message yet, so CTC does not send it; it records the release as
/// pending placement instead (see the integration guide in docs/integration). When the Train
/// Model gains a release receiver it should acknowledge with a correlated response before CTC
/// reports the train as physically released.
/// </para>
/// <para>Switch changes are sent with <see cref="SwitchPositionRequestMessage"/>, not here.</para>
/// </remarks>
public class MovementRequestMessage
{
    public string TrainId { get; set; } = string.Empty;

    public MovementRequestType RequestType { get; set; } = MovementRequestType.ReleaseTrain;
}
