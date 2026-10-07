namespace CTC.Core.Models;

/// <summary>
/// One block on a <see cref="ScheduledTrain"/>'s route and the time the train is
/// scheduled to ENTER it.
/// </summary>
public class ScheduledBlockTime
{
    public string BlockId { get; set; } = string.Empty;

    /// <summary>Time of simulation day at which the train enters the block.</summary>
    public TimeSpan ArrivalTime { get; set; }
}
