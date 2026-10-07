namespace CTC.Core.Scheduling;

/// <summary>
/// A schedule in spreadsheet form: one row per block of a line, one time column per
/// train. Both the manual Schedule Builder and a future spreadsheet import fill one of
/// these, and <see cref="ScheduleTemplateConverter"/> turns it into the normalized
/// <see cref="Models.ScheduledTrain"/> objects CTC actually stores. The template itself
/// is an input format only; it is never kept in <see cref="Models.CtcSystemState"/>.
/// </summary>
public sealed class ScheduleTemplate
{
    public ScheduleTemplate(string lineId, IEnumerable<string> trainIds, IEnumerable<ScheduleTemplateRow> rows)
    {
        LineId = lineId;
        TrainIds = trainIds.ToList();
        Rows = rows.ToList();
    }

    public string LineId { get; }

    /// <summary>One entry per train column, in column order.</summary>
    public IReadOnlyList<string> TrainIds { get; }

    /// <summary>One row per block, in layout order.</summary>
    public IReadOnlyList<ScheduleTemplateRow> Rows { get; }
}

/// <summary>
/// One block's row in a <see cref="ScheduleTemplate"/>.
/// </summary>
public sealed class ScheduleTemplateRow
{
    public ScheduleTemplateRow(int trainCount)
    {
        TrainTimes = Enumerable.Repeat(string.Empty, trainCount).ToArray();
    }

    public string LineId { get; init; } = string.Empty;

    public string Section { get; init; } = string.Empty;

    public string BlockId { get; init; } = string.Empty;

    public int BlockNumber { get; init; }

    /// <summary>Empty when the block has no station.</summary>
    public string StationName { get; init; } = string.Empty;

    /// <summary>Readable summary of the block's features, e.g. "Station B; Signal".</summary>
    public string Infrastructure { get; init; } = string.Empty;

    public double LengthMeters { get; init; }

    /// <summary>Read-only track data shown to the dispatcher; not editable in a schedule.</summary>
    public double SpeedLimitKilometersPerHour { get; init; }

    /// <summary>Blocks this block connects to, used to check each train's route is continuous.</summary>
    public IReadOnlyList<string> ConnectedBlockIds { get; init; } = Array.Empty<string>();

    /// <summary>The train's time in this row is its departure (route start) time.</summary>
    public bool IsRouteStart { get; init; }

    public bool IsStation => !string.IsNullOrWhiteSpace(StationName);

    /// <summary>
    /// Time text per train column, as entered (expected HH:mm:ss): the time the train ENTERS
    /// this block. Every row accepts a time. Blank means the train does not use this block.
    /// </summary>
    public string[] TrainTimes { get; }
}
