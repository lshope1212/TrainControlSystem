namespace CTC.Core.Scheduling;

/// <summary>
/// A schedule in spreadsheet form: one row per block of a line, one time column per
/// train. Both the manual Schedule Builder and the CSV import
/// (<see cref="ScheduleCsvImporter"/>) fill one of these, and <see cref="ScheduleTemplateConverter"/> turns it into the normalized
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
        TrainDwellTimes = Enumerable.Repeat(string.Empty, trainCount).ToArray();
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
    /// this block. Every row accepts a time. Blank means no time is scheduled here: the train
    /// either passes through (when the block lies between two timed blocks) or does not use it.
    /// </summary>
    public string[] TrainTimes { get; }

    /// <summary>
    /// Dwell time text per train column, in whole seconds: how long the train stays in this
    /// block after entering it. Blank means the block's <see cref="DefaultDwellTime"/>. Only
    /// meaningful where the train has a time; validated by <see cref="ScheduleTemplateConverter"/>.
    /// </summary>
    public string[] TrainDwellTimes { get; }

    /// <summary>
    /// Dwell a train gets here unless the dispatcher overrides it, from
    /// <see cref="DwellTimePolicy"/> (45 s at a station, otherwise 0 s). Never applies to the
    /// route start block, which has no dwell.
    /// </summary>
    public TimeSpan DefaultDwellTime { get; init; }

    /// <summary>
    /// Sets a train's time in this block the way the dispatcher selecting a destination does:
    /// when the block BECOMES timed for that train (blank to a time), its dwell is reset to
    /// <see cref="DefaultDwellTime"/>; when the time is cleared, so is the dwell. Editing an
    /// existing time keeps the dwell, so a manually entered dwell is never overwritten.
    /// </summary>
    public void SetTrainTime(int column, string? text)
    {
        text ??= string.Empty;
        bool wasTimed = !string.IsNullOrWhiteSpace(TrainTimes[column]);
        bool isTimed = !string.IsNullOrWhiteSpace(text);
        TrainTimes[column] = text;

        if (!isTimed)
        {
            TrainDwellTimes[column] = string.Empty;
        }
        else if (!wasTimed)
        {
            TrainDwellTimes[column] = IsRouteStart ? string.Empty : DwellTimePolicy.FormatSeconds(DefaultDwellTime);
        }
    }

    /// <summary>
    /// Sets a train's dwell in this block from text in whole seconds; blank restores the default.
    /// A negative or non-numeric value, or any dwell at the route start block, is rejected and
    /// the previous value kept.
    /// </summary>
    /// <returns>A dispatcher-readable error, or null when the value was stored.</returns>
    public string? TrySetTrainDwellTime(int column, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            TrainDwellTimes[column] = string.Empty;
            return null;
        }

        string? error = DwellTimePolicy.TryParseSeconds(text, out var dwell);
        if (error is not null)
        {
            return error;
        }

        if (IsRouteStart && dwell > TimeSpan.Zero)
        {
            return $"{BlockId} is the route start block; trains depart it at their departure time, so it has no dwell time.";
        }

        TrainDwellTimes[column] = DwellTimePolicy.FormatSeconds(dwell);
        return null;
    }
}
