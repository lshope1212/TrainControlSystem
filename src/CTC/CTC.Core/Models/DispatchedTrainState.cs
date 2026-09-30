namespace CTC.Core.Models;

/// <summary>
/// CTC's view of a train that is operating on the line.
/// </summary>
public class DispatchedTrainState
{
    public string TrainId { get; set; } = string.Empty;

    /// <summary>Speed authorized by the Track Controller (not CTC's suggestion).</summary>
    public double AuthorizedSpeedMetersPerSecond { get; set; }

    /// <summary>Authority authorized by the Track Controller (not CTC's suggestion).</summary>
    public double AuthorizedAuthorityMeters { get; set; }

    /// <summary>
    /// Block the train currently occupies; empty when unknown.
    /// KNOWN GAP: block occupancy from the Track Controller carries no train ID, so
    /// this cannot yet be populated authoritatively. CTC must not guess it.
    /// </summary>
    public string CurrentBlockId { get; set; } = string.Empty;

    public string NextStation { get; set; } = string.Empty;
}
