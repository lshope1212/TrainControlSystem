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

        var envelope = message as MessageEnvelope ?? CreateEnvelope(message.GetType().Name, message);

        return JsonSerializer.Serialize(envelope, Options);
    }

    /// <summary>Names an operation with plain data, without requiring a class for that operation.</summary>
    public static MessageEnvelope CreateEnvelope(string messageType, object payload) => new()
    {
        MessageType = messageType,
        Payload = JsonSerializer.SerializeToElement(payload, payload.GetType(), Options),
    };

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

    /// <summary>Reads a named value from an operation payload, using the shared enum/JSON rules.</summary>
    public static TValue ReadValue<TValue>(MessageEnvelope envelope, string propertyName)
        where TValue : notnull
    {
        if (!envelope.Payload.TryGetProperty(propertyName, out var payload))
        {
            throw new JsonException($"Payload of '{envelope.MessageType}' has no '{propertyName}'.");
        }
        return payload.Deserialize<TValue>(Options)
            ?? throw new JsonException($"Payload of '{envelope.MessageType}' has an empty '{propertyName}'.");
    }
}
