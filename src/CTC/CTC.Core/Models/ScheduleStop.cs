namespace CTC.Core.Models;

/// <summary>
/// One station stop within a <see cref="ScheduledTrain"/>.
/// </summary>
public class ScheduleStop
{
    /// <summary>Block containing the station, so routing need not rely on display names.</summary>
    public string BlockId { get; set; } = string.Empty;

    public string StationName { get; set; } = string.Empty;

    /// <summary>Arrival time as time of simulation day.</summary>
    public TimeSpan ArrivalTime { get; set; }
}
