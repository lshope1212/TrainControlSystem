namespace TrainControl.Contracts.Messages;

/// <summary>Explicit Murphy failure settings for a block; sending the same settings is safe.</summary>
public sealed class TrackModelFailureCommandMessage
{
    public string BlockId { get; set; } = string.Empty;
    public bool BrokenRail { get; set; }
    public bool TrackCircuitFailure { get; set; }
    public bool PowerFailure { get; set; }
}
