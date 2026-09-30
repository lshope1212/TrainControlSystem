namespace TrainControl.Contracts.Messages;

/// <summary>
/// Speed and authority the wayside has actually authorized for a train.
/// Direction: Track Controller -> CTC.
/// </summary>
public class TrainAuthorizationStatusMessage
{
    public string TrainId { get; set; } = string.Empty;

    public double AuthorizedSpeedMetersPerSecond { get; set; }

    public double AuthorizedAuthorityMeters { get; set; }
}
