namespace CTC.Core.Models;

/// <summary>
/// A train entered into the CTC schedule by the dispatcher.
/// </summary>
public class ScheduledTrain
{
    public string TrainId { get; set; } = string.Empty;

    /// <summary>Departure time as time of simulation day.</summary>
    public TimeSpan DepartureTime { get; set; }

    public IList<ScheduleStop> Stops { get; } = new List<ScheduleStop>();
}
