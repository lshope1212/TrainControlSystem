namespace CTC.Core.Models;

/// <summary>
/// One station stop within a <see cref="ScheduledTrain"/>.
/// </summary>
public class ScheduleStop
{
    public string StationName { get; set; } = string.Empty;

    /// <summary>Arrival time as time of simulation day.</summary>
    public TimeSpan ArrivalTime { get; set; }
}
