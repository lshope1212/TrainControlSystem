using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrainControl.Common.Communication;

/// <summary>
/// The single implementation of the inter-process message format: one
/// <see cref="MessageEnvelope"/> per line, camelCase JSON, enums as readable strings.
/// Every sender and receiver must use this so both directions stay compatible.
/// </summary>
public static class MessageSerializer
{
    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Wraps <paramref name="message"/> in an envelope named after its runtime type.</summary>
    public static string Serialize(object message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var envelope = new MessageEnvelope
        {
            MessageType = message.GetType().Name,
            Payload = JsonSerializer.SerializeToElement(message, message.GetType(), Options),
        };

        return JsonSerializer.Serialize(envelope, Options);
    }

    /// <exception cref="JsonException">The text is not a valid envelope.</exception>
    public static MessageEnvelope Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var envelope = JsonSerializer.Deserialize<MessageEnvelope>(json, Options);
        if (envelope is null || string.IsNullOrWhiteSpace(envelope.MessageType))
        {
            throw new JsonException("Message envelope has no messageType.");
        }

        if (envelope.Payload.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"Message envelope for '{envelope.MessageType}' has no payload object.");
        }

        return envelope;
    }

    /// <exception cref="JsonException">The payload does not match <typeparamref name="TMessage"/>.</exception>
    public static TMessage DeserializePayload<TMessage>(MessageEnvelope envelope)
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return envelope.Payload.Deserialize<TMessage>(Options)
            ?? throw new JsonException($"Payload of '{envelope.MessageType}' is empty.");
    }
}
