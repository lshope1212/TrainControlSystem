namespace TrainControl.Contracts.Messages;

/// <summary>Optional feedback to the standalone tester after an input is processed.</summary>
public sealed class TrackModelInputResultMessage
{
    public string MessageType { get; set; } = string.Empty;
    public bool Accepted { get; set; }
    public string Detail { get; set; } = string.Empty;
}
