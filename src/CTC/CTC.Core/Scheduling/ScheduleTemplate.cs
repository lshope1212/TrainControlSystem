namespace CTC.Core.Scheduling;

/// <summary>
/// A schedule in spreadsheet form: one row per block of a line, one time column per
/// train. Both the manual Schedule Builder and the CSV import
/// (<see cref="ScheduleCsvImporter"/>) fill one of these, and <see cref="ScheduleTemplateConverter"/> turns it into the normalized
/// <see cref="Models.ScheduledTrain"/> objects CTC actually stores. The template itself
/// is an input format only; it is never kept in <see cref="Models.CtcSystemState"/>.
/// </summary>
/// <remarks>
/// Each train has its own starting block, chosen from the line's yard entries
/// (<see cref="YardEntryBlockIds"/>, see <see cref="YardEntries"/>). That train's time in its
/// starting block's row is its departure time; its times in every other row are arrival times.
/// </remarks>
public sealed class ScheduleTemplate
{
    private readonly string[] _startBlockIds;
    private readonly bool[] _departureUnconfirmed;
    private readonly HashSet<(string BlockId, int Column)> _timesToReconsider = new();

    public ScheduleTemplate(string lineId, IEnumerable<string> trainIds, IEnumerable<ScheduleTemplateRow> rows)
        : this(lineId, trainIds, rows, Array.Empty<string>())
    {
    }

    /// <param name="yardEntryBlockIds">The line's yard entries when the template was made: the only permitted starting blocks.</param>
    /// <param name="lineName">Display name used in messages; the line ID when blank.</param>
    public ScheduleTemplate(string lineId, IEnumerable<string> trainIds, IEnumerable<ScheduleTemplateRow> rows, IEnumerable<string> yardEntryBlockIds, string? lineName = null)
    {
        LineId = lineId;
        LineName = string.IsNullOrWhiteSpace(lineName) ? $"Line {lineId}" : lineName;
        TrainIds = trainIds.ToList();
        Rows = rows.ToList();
        YardEntryBlockIds = yardEntryBlockIds.Where(id => Rows.Any(row => row.BlockId == id)).Distinct(StringComparer.Ordinal).ToList();
        _startBlockIds = Enumerable.Repeat(string.Empty, TrainIds.Count).ToArray();
        _departureUnconfirmed = new bool[TrainIds.Count];

        foreach (var row in Rows)
        {
            row.Template = this;
        }
    }

    public string LineId { get; }

    public string LineName { get; }

    /// <summary>One entry per train column, in column order.</summary>
    public IReadOnlyList<string> TrainIds { get; }

    /// <summary>One row per block, in layout order.</summary>
    public IReadOnlyList<ScheduleTemplateRow> Rows { get; }

    /// <summary>
    /// The line's yard-entry blocks, in layout order: the only blocks a train may start from.
    /// Empty when the layout states none, in which case no train in this template can be queued.
    /// </summary>
    public IReadOnlyList<string> YardEntryBlockIds { get; }

    /// <summary>The train's selected starting block; empty when none is selected yet.</summary>
    public string GetStartBlockId(int column) => _startBlockIds[column];

    /// <summary>The row of the train's selected starting block, or null when none is selected.</summary>
    public ScheduleTemplateRow? GetStartRow(int column) =>
        _startBlockIds[column].Length == 0 ? null : Rows.FirstOrDefault(row => row.BlockId == _startBlockIds[column]);

    /// <summary>
    /// True when the time in the train's starting block was entered as an ARRIVAL time, before that
    /// block became its start. It is not taken as the departure until the dispatcher re-enters it.
    /// </summary>
    public bool IsDepartureUnconfirmed(int column) => _departureUnconfirmed[column];

    /// <summary>
    /// Whether the dispatcher should look at this train's time in <paramref name="row"/> again because
    /// a starting-block change moved or affected it. Cleared when that time is edited.
    /// </summary>
    public bool NeedsReconsidering(ScheduleTemplateRow row, int column) => _timesToReconsider.Contains((row.BlockId, column));

    /// <summary>Readable label of a row's block, e.g. "K63 (GREEN-63)".</summary>
    public static string Label(ScheduleTemplateRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        string label = $"{row.Section}{row.BlockNumber}";
        return label == row.BlockId ? label : $"{label} ({row.BlockId})";
    }

    /// <summary>
    /// Chooses the train's starting block, as the dispatcher does in the Schedule Builder. Only one of
    /// <see cref="YardEntryBlockIds"/> is accepted. The train's departure time moves with the start:
    /// it is taken out of the old starting block (so it is never read there as an arrival time) and put
    /// into the new one. Every time the change affects is flagged (<see cref="NeedsReconsidering"/>),
    /// and the result says what moved, so nothing changes silently. The schedule is fully revalidated
    /// when it is converted and queued.
    /// </summary>
    public StartBlockChange SelectStartBlock(int column, string? blockId)
    {
        string trainName = Models.TrainIds.DisplayName(TrainIds[column]);
        blockId = blockId?.Trim() ?? string.Empty;

        if (YardEntryBlockIds.Count == 0)
        {
            return StartBlockChange.Rejected($"{trainName}: {LineName} has no yard-entry blocks in its track layout, so no starting block can be selected.");
        }

        var newRow = Rows.FirstOrDefault(row => row.BlockId == blockId);
        if (newRow is null || !YardEntryBlockIds.Contains(blockId))
        {
            string shown = newRow is null ? (blockId.Length == 0 ? "no block" : blockId) : Label(newRow);
            return StartBlockChange.Rejected($"{trainName} cannot start at {shown}: trains enter {LineName} from the yard only at {DescribeYardEntries()}.");
        }

        var oldRow = GetStartRow(column);
        if (oldRow == newRow)
        {
            return StartBlockChange.Unchanged();
        }

        string departure = oldRow?.TrainTimes[column] ?? string.Empty;
        string previousAtNew = newRow.TrainTimes[column];
        var notes = new List<string>();

        _startBlockIds[column] = blockId;
        _departureUnconfirmed[column] = false;
        ClearReconsider(column);

        if (oldRow is not null && departure.Length > 0)
        {
            // The old departure time must not stay behind, where it would read as an arrival time.
            oldRow.TrainTimes[column] = string.Empty;
            oldRow.TrainDwellTimes[column] = string.Empty;
        }

        if (departure.Length > 0)
        {
            newRow.TrainTimes[column] = departure;
            newRow.TrainDwellTimes[column] = string.Empty;
            _timesToReconsider.Add((newRow.BlockId, column));
            notes.Add($"its departure time {departure.Trim()} moved from {Label(oldRow!)} to {Label(newRow)}");
            if (previousAtNew.Length > 0 && previousAtNew != departure)
            {
                notes.Add($"the arrival time {previousAtNew.Trim()} it had at {Label(newRow)} was replaced, because a train departs its starting block rather than arriving there");
            }
        }
        else if (previousAtNew.Length > 0 && oldRow is null)
        {
            // No start was selected when it was typed, so it can only have been meant as the departure.
            newRow.TrainDwellTimes[column] = string.Empty;
            _timesToReconsider.Add((newRow.BlockId, column));
            notes.Add($"its time {previousAtNew.Trim()} at {Label(newRow)} is now its departure time");
        }
        else if (previousAtNew.Length > 0)
        {
            // Entered as an arrival time while another block was the start; not reinterpreted as the
            // departure without the dispatcher saying so.
            newRow.TrainDwellTimes[column] = string.Empty;
            _departureUnconfirmed[column] = true;
            _timesToReconsider.Add((newRow.BlockId, column));
            notes.Add($"its time {previousAtNew.Trim()} at {Label(newRow)} was entered as an arrival time; re-enter it to confirm it as the departure time");
        }

        // Arrival times not after the (new) departure can no longer be right.
        if (ScheduleTemplateConverter.TryParseTime(newRow.TrainTimes[column], out var departs))
        {
            foreach (var row in Rows.Where(row => row != newRow))
            {
                if (ScheduleTemplateConverter.TryParseTime(row.TrainTimes[column], out var time) && time <= departs)
                {
                    _timesToReconsider.Add((row.BlockId, column));
                    notes.Add($"its arrival at {Label(row)} ({row.TrainTimes[column].Trim()}) is not after the departure");
                }
            }
        }

        string message = $"{trainName} now starts at {Label(newRow)}"
            + (notes.Count == 0 ? "." : $"; {string.Join("; ", notes)}.")
            + " The route is rebuilt from the new start when the schedule is queued; check the highlighted times.";
        return StartBlockChange.Changed(message);
    }

    /// <summary>
    /// Sets the starting block without moving any times: used while a template is being filled
    /// (the factory's single-entry default, the CSV import's unambiguous start) before the train has times.
    /// </summary>
    internal void InitializeStartBlock(int column, string blockId)
    {
        if (!YardEntryBlockIds.Contains(blockId))
        {
            throw new ArgumentException($"{blockId} is not a yard entry of {LineName}.", nameof(blockId));
        }

        _startBlockIds[column] = blockId;
    }

    /// <summary>The line's yard entries as a readable list, e.g. "K63 (GREEN-63)".</summary>
    public string DescribeYardEntries() => YardEntryBlockIds.Count == 0
        ? "no yard entries"
        : string.Join(", ", YardEntryBlockIds.Select(id => Label(Rows.First(row => row.BlockId == id))));

    /// <summary>The dispatcher edited this train's time in <paramref name="row"/>, so it has been reconsidered.</summary>
    internal void OnTimeEdited(ScheduleTemplateRow row, int column)
    {
        _timesToReconsider.Remove((row.BlockId, column));
        if (row.BlockId == _startBlockIds[column])
        {
            _departureUnconfirmed[column] = false;
        }
    }

    private void ClearReconsider(int column) => _timesToReconsider.RemoveWhere(cell => cell.Column == column);
}

/// <summary>Outcome of <see cref="ScheduleTemplate.SelectStartBlock"/>.</summary>
public sealed class StartBlockChange
{
    private StartBlockChange(bool isAccepted, bool isChanged, string? message)
    {
        IsAccepted = isAccepted;
        IsChanged = isChanged;
        Message = message;
    }

    /// <summary>False when the block is not a permitted starting block; nothing changed.</summary>
    public bool IsAccepted { get; }

    /// <summary>True when the train's starting block changed.</summary>
    public bool IsChanged { get; }

    /// <summary>Dispatcher-readable description of what changed, or why it was refused; null when unchanged.</summary>
    public string? Message { get; }

    internal static StartBlockChange Rejected(string message) => new(false, false, message);

    internal static StartBlockChange Unchanged() => new(true, false, null);

    internal static StartBlockChange Changed(string message) => new(true, true, message);
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

    /// <summary>The template this row belongs to; set by the template's constructor.</summary>
    internal ScheduleTemplate? Template { get; set; }

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

    /// <summary>The block is one of the line's yard entries, so a train may start here.</summary>
    public bool IsYardEntry => Template?.YardEntryBlockIds.Contains(BlockId) == true;

    /// <summary>
    /// The train in <paramref name="column"/> starts its route here, so its time in this row is
    /// its departure time. Each train has its own starting block.
    /// </summary>
    public bool IsRouteStartFor(int column) => Template?.GetStartBlockId(column) == BlockId;

    public bool IsStation => !string.IsNullOrWhiteSpace(StationName);

    /// <summary>
    /// Time text per train column, as entered (expected HH:mm:ss): the time the train ENTERS
    /// this block, or, in the train's starting block, its departure time. Every row accepts a time.
    /// Blank means no time is scheduled here: the train either passes through (when the block lies
    /// between two timed blocks) or does not use it.
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
    /// <see cref="DwellTimePolicy"/> (45 s at a station, otherwise 0 s). Never applies to a
    /// train's starting block, which has no dwell.
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
        Template?.OnTimeEdited(this, column);

        if (!isTimed)
        {
            TrainDwellTimes[column] = string.Empty;
        }
        else if (!wasTimed)
        {
            TrainDwellTimes[column] = IsRouteStartFor(column) ? string.Empty : DwellTimePolicy.FormatSeconds(DefaultDwellTime);
        }
    }

    /// <summary>
    /// Sets a train's dwell in this block from text in whole seconds; blank restores the default.
    /// A negative or non-numeric value, or any dwell at the train's starting block, is rejected and
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

        if (IsRouteStartFor(column) && dwell > TimeSpan.Zero)
        {
            return $"{BlockId} is this train's starting block; it departs at its departure time, so it has no dwell time.";
        }

        TrainDwellTimes[column] = DwellTimePolicy.FormatSeconds(dwell);
        return null;
    }
}
