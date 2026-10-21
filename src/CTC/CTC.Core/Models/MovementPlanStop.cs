namespace CTC.Core.Models;

/// <summary>
/// One remaining stop (timed destination) in a dispatched train's Active Movement Plan:
/// <c>ARRIVE → dwell → DEPART → travel to the next stop</c>.
/// </summary>
public class MovementPlanStop
{
    public string BlockId { get; set; } = string.Empty;

    /// <summary>Time of simulation day the train is planned to ENTER <see cref="BlockId"/>.</summary>
    public TimeSpan ArrivalTime { get; set; }

    /// <summary>How long the train stays in the block before starting the next movement; zero means it does not stop.</summary>
    public TimeSpan DwellTime { get; set; }

    /// <summary>Planned departure: <c>ArrivalTime + DwellTime</c>. Dwell never moves the arrival itself.</summary>
    public TimeSpan DepartureTime => ArrivalTime + DwellTime;

    /// <summary>Position of <see cref="BlockId"/> in <see cref="DispatchedTrainState.ActiveRouteBlockIds"/>.</summary>
    public int RouteIndex { get; set; }

    /// <summary>
    /// Speed planned for the movement that ENDS at this stop. For the stop the train is
    /// currently travelling to, the speed CTC last sent; for later stops, the speed calculated
    /// from the previous stop's departure to this stop's arrival (sent when that movement starts).
    /// </summary>
    public double PlannedSpeedMetersPerSecond { get; set; }

    /// <summary>Why the movement ending here cannot be made on time (late or capped), or null.</summary>
    public string? TimingWarning { get; set; }
}
