namespace TrainControl.Contracts.Messages;

/// <summary>
/// Simulation-only block terrain delivered from the Track Model to the Train
/// Model so the plant can compute the gravitational load on the consist.
/// Metric on the wire. This flow exists to simulate the physical world and is
/// discarded when the real track replaces the Track Model at install.
/// </summary>
public class TrackConditionMessage
{
    public string TrainId { get; set; } = string.Empty;

    /// <summary>Grade of the current block as a percentage (rise/run * 100).
    /// Positive = uphill in the train's direction of travel.</summary>
    public double GradePercent { get; set; }

    /// <summary>Block elevation in meters (informational / display).</summary>
    public double ElevationMeters { get; set; }
}
