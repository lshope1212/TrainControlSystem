namespace CTC.Core.Models;

/// <summary>
/// One train's schedule on one line: the ordered blocks of its route and when it enters
/// each one. This normalized form is what CTC stores, whether the schedule was built
/// manually or (in future) imported from a spreadsheet.
/// </summary>
public class ScheduledTrain
{
    public string TrainId { get; set; } = string.Empty;

    public string LineId { get; set; } = string.Empty;

    /// <summary>
    /// The train's route in travel order, each block with the time the train enters it.
    /// The first entry is the route-start block; consecutive entries are connected blocks.
    /// Blocks the train does not use (e.g. another branch) are absent.
    /// </summary>
    public IList<ScheduledBlockTime> BlockTimes { get; } = new List<ScheduledBlockTime>();

    /// <summary>Block the train's route starts from (the first block time); empty if there is none.</summary>
    public string StartBlockId => BlockTimes.Count > 0 ? BlockTimes[0].BlockId : string.Empty;

    /// <summary>
    /// Departure time as time of simulation day: the time the train enters its
    /// route-start block. Not entered on its own; it is the first block time.
    /// </summary>
    public TimeSpan DepartureTime => BlockTimes.Count > 0 ? BlockTimes[0].ArrivalTime : TimeSpan.Zero;
}
