namespace TrainControl.Contracts.Messages;

/// <summary>
/// Placeholder message carrying the movement authority granted to a train.
/// </summary>
public class AuthorityMessage
{
    public string TrainId { get; set; } = string.Empty;

    public double AuthorityMeters { get; set; }
}
