namespace TrainControl.Contracts.Messages;

/// <summary>
/// Suggested speed and authority for a train.
/// Direction: CTC -> Track Controller.
/// </summary>
public class MovementSuggestionMessage
{
    public string TrainId { get; set; } = string.Empty;

    public double SuggestedSpeedMetersPerSecond { get; set; }

    public double SuggestedAuthorityMeters { get; set; }
}
