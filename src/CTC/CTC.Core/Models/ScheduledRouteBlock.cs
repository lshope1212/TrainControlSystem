namespace CTC.Core.Models;

/// <summary>
/// One block on a <see cref="ScheduledTrain"/>'s route. A timed block is one the dispatcher
/// entered a time for (a waypoint); an untimed block is one CTC routed the train through
/// to connect two waypoints.
/// </summary>
public class ScheduledRouteBlock
{
    public string BlockId { get; set; } = string.Empty;

    /// <summary>
    /// Time of simulation day at which the train is scheduled to ENTER the block; null when
    /// the train only passes through it on the way to its next timed block.
    /// </summary>
    public TimeSpan? ArrivalTime { get; set; }

    public bool IsTimed => ArrivalTime.HasValue;
}
