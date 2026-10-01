using System.Text.Json;
using System.Text.Json.Serialization;

namespace CTC.TestUI.Wpf.Models;

/// <summary>
/// One outgoing message that CTC.Core handed to the <see cref="Services.RecordingMessageSender"/>.
/// Keeps the actual strongly typed contract object alongside a readable description.
/// </summary>
public sealed class RecordedMessage
{
    // Display-only formatting; this is not a transport format.
    private static readonly JsonSerializerOptions DescriptionOptions = new JsonSerializerOptions
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public RecordedMessage(int sequenceNumber, DateTime capturedAt, object payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        SequenceNumber = sequenceNumber;
        CapturedAt = capturedAt;
        Payload = payload;
        MessageType = payload.GetType().Name;
        Description = JsonSerializer.Serialize(payload, payload.GetType(), DescriptionOptions);
    }

    public int SequenceNumber { get; }

    /// <summary>Wall-clock capture time (not simulation time).</summary>
    public DateTime CapturedAt { get; }

    /// <summary>Contract type name, e.g. MaintenanceRequestMessage.</summary>
    public string MessageType { get; }

    /// <summary>The actual TrainControl.Contracts object CTC.Core tried to send.</summary>
    public object Payload { get; }

    /// <summary>Readable rendering of the payload's properties.</summary>
    public string Description { get; }
}
