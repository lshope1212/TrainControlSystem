namespace CTC.Core.Models;

/// <summary>
/// One train's schedule on one line: where it starts, when it departs, and the station
/// stops it is scheduled to make. This normalized form is what CTC stores, whether the
/// schedule was built manually or (in future) imported from a spreadsheet.
/// </summary>
public class ScheduledTrain
{
    public string TrainId { get; set; } = string.Empty;

    public string LineId { get; set; } = string.Empty;

    /// <summary>Block the train's route starts from.</summary>
    public string StartBlockId { get; set; } = string.Empty;

    /// <summary>
    /// Departure (route start) time as time of simulation day. Not entered on its own:
    /// it is derived from the train's timestamp at the route-start block.
    /// </summary>
    public TimeSpan DepartureTime { get; set; }

    /// <summary>Scheduled station stops in route order. Stations the train skips have no stop.</summary>
    public IList<ScheduleStop> Stops { get; } = new List<ScheduleStop>();
}
