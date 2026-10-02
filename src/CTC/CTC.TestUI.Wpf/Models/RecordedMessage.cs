using System.Text.Json;
using TrainControl.Common.Communication;

namespace CTC.TestUI.Wpf.Models;

/// <summary>
/// One row of the Captured CTC Outputs list: a message the running CTC actually sent over
/// the Track Controller pipe, or a receive error.
/// </summary>
public sealed class RecordedMessage
{
    private RecordedMessage(int sequenceNumber, string messageType, string description)
    {
        SequenceNumber = sequenceNumber;
        CapturedAt = DateTime.Now;
        MessageType = messageType;
        Description = description;
    }

    public int SequenceNumber { get; }

    /// <summary>Wall-clock receive time (not simulation time).</summary>
    public DateTime CapturedAt { get; }

    /// <summary>Contract type name from the envelope, e.g. MaintenanceRequestMessage.</summary>
    public string MessageType { get; }

    /// <summary>Readable rendering of the payload, e.g. "BlockId = G12, RequestedState = Closed".</summary>
    public string Description { get; }

    public static RecordedMessage FromEnvelope(int sequenceNumber, MessageEnvelope envelope) =>
        new RecordedMessage(sequenceNumber, envelope.MessageType, Describe(envelope.Payload));

    public static RecordedMessage FromError(int sequenceNumber, string error) =>
        new RecordedMessage(sequenceNumber, "(receive error)", error);

    // Display only; the payload is shown as received rather than deserialized into a contract,
    // so an unexpected message type is still visible.
    private static string Describe(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return payload.GetRawText();
        }

        return string.Join(", ", payload.EnumerateObject().Select(property =>
            $"{Capitalize(property.Name)} = {(property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText())}"));
    }

    private static string Capitalize(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];
}
