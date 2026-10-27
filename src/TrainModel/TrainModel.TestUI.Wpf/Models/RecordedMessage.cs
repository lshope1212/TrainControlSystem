namespace TrainModel.TestUI.Wpf.Models;

/// <summary>A captured outbound message from the Train Model, for the harness grid.</summary>
public sealed class RecordedMessage
{
    public int SequenceNumber { get; init; }
    public DateTime CapturedAt { get; init; } = DateTime.Now;
    public string MessageType { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
}
