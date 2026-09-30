namespace CTC.Core.Models;

/// <summary>
/// Aggregate of everything the CTC office currently knows about the railway.
/// Built by <see cref="Services.CTCService"/> from shared contract messages; it never
/// stores another subsystem's messages or internal types directly.
/// </summary>
public class CtcSystemState
{
    public IList<CtcLineState> Lines { get; } = new List<CtcLineState>();

    public IList<ScheduledTrain> ScheduledTrains { get; } = new List<ScheduledTrain>();

    public IList<DispatchQueueEntry> DispatchQueue { get; } = new List<DispatchQueueEntry>();

    public IList<DispatchedTrainState> DispatchedTrains { get; } = new List<DispatchedTrainState>();

    /// <summary>
    /// Current simulation time of day. Set externally from the shared simulation
    /// clock; CTC does not advance it on its own.
    /// </summary>
    public TimeSpan SystemTime { get; set; }

    public CtcLineState? FindLine(string lineId) => Lines.FirstOrDefault(line => line.LineId == lineId);

    /// <summary>
    /// Finds a block by ID across all lines. Block IDs are assumed to be unique
    /// across the whole layout because block status messages carry no line ID.
    /// </summary>
    public CtcBlockState? FindBlock(string blockId) => Lines.SelectMany(line => line.Blocks).FirstOrDefault(block => block.BlockId == blockId);

    public DispatchedTrainState? FindDispatchedTrain(string trainId) => DispatchedTrains.FirstOrDefault(train => train.TrainId == trainId);
}
