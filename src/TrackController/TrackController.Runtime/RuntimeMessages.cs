using TrackController.Contracts.Messages;
using TrainControl.Common.Communication;

namespace TrackController.Runtime;

/// <summary>Creates envelopes and rejection receipts using the existing team message format.</summary>
public static class RuntimeMessages
{
    /// <summary>Uses the common serializer to construct the same envelope accepted by module receivers.</summary>
    public static MessageEnvelope CreateEnvelope(object value) => MessageSerializer.Deserialize(MessageSerializer.Serialize(value));

    /// <summary>Creates an application rejection receipt without implying transport failure.</summary>
    public static WaysideAcknowledgementMessage CreateRejection(string type, string detail) => new(false, detail, type);
}
