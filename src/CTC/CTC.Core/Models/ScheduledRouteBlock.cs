namespace CTC.Core.Models;

/// <summary>
/// One block on a <see cref="ScheduledTrain"/>'s route. A timed block is one the dispatcher
/// entered a time for (a waypoint, or stop); an untimed block is one CTC routed the train
/// through to connect two waypoints.
/// </summary>
public class ScheduledRouteBlock
{
    public string BlockId { get; set; } = string.Empty;

    /// <summary>
    /// Time of simulation day at which the train is scheduled to ENTER the block; null when
    /// the train only passes through it on the way to its next timed block.
    /// </summary>
    public TimeSpan? ArrivalTime { get; set; }

    /// <summary>
    /// How long the train stays in this block after entering it before it starts the next
    /// movement (see <see cref="Scheduling.DwellTimePolicy"/>). Zero for untimed blocks and the
    /// route start block, and for timed blocks the train does not intentionally stop at.
    /// </summary>
    public TimeSpan DwellTime { get; set; }

    public bool IsTimed => ArrivalTime.HasValue;

    /// <summary>When the train is scheduled to leave the block: <c>ArrivalTime + DwellTime</c>; null when untimed.</summary>
    public TimeSpan? DepartureTime => ArrivalTime + DwellTime;
}
