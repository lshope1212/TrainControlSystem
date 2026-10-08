namespace CTC.Core.Models;

/// <summary>
/// One train's schedule on one line: the ordered blocks of its route and when it enters
/// the timed ones. This normalized form is what CTC stores, whether the schedule was built
/// manually or (in future) imported from a spreadsheet.
/// </summary>
public class ScheduledTrain
{
    public string TrainId { get; set; } = string.Empty;

    public string LineId { get; set; } = string.Empty;

    /// <summary>
    /// Every block the train travels through, in travel order; consecutive entries are
    /// connected blocks. The first and last blocks are always timed; blocks between timed
    /// blocks may be untimed (routed through). Blocks the train does not use are absent.
    /// </summary>
    public IList<ScheduledRouteBlock> Route { get; } = new List<ScheduledRouteBlock>();

    /// <summary>Block the train's route starts from; empty if there is none.</summary>
    public string StartBlockId => Route.Count > 0 ? Route[0].BlockId : string.Empty;

    /// <summary>
    /// Departure time as time of simulation day: the time the train enters its
    /// route-start block. Not entered on its own; it is the first block's time.
    /// </summary>
    public TimeSpan DepartureTime => Route.Count > 0 ? Route[0].ArrivalTime ?? TimeSpan.Zero : TimeSpan.Zero;
}
