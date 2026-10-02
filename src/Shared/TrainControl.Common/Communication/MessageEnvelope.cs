using System.Text.Json;

namespace TrainControl.Common.Communication;

/// <summary>
/// Transport framing around one shared-contract message, e.g.
/// {"messageType":"MaintenanceRequestMessage","payload":{"blockId":"G12","requestedState":"Closed"}}
/// </summary>
/// <remarks>
/// <see cref="MessageType"/> is the contract's class name. The payload is kept as raw
/// JSON so a receiver can inspect the type before choosing what to deserialize it into
/// (see <see cref="MessageSerializer.DeserializePayload{TMessage}"/>).
/// </remarks>
public sealed class MessageEnvelope
{
    public string MessageType { get; init; } = string.Empty;

    public JsonElement Payload { get; init; }
}
