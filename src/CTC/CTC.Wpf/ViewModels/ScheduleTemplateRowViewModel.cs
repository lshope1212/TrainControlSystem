using System.Collections.ObjectModel;
using CTC.Core.Models;
using CTC.Core.Scheduling;
using TrainControl.Common.Utilities;

namespace CTC.Wpf.ViewModels;

/// <summary>
/// One block's row in the Schedule Builder grid. Wraps a <see cref="ScheduleTemplateRow"/>;
/// the train time cells write straight through to it.
/// </summary>
public class ScheduleTemplateRowViewModel
{
    public ScheduleTemplateRowViewModel(ScheduleTemplate template, ScheduleTemplateRow row, Action<string>? reportError = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        Row = row ?? throw new ArgumentNullException(nameof(row));

        for (int column = 0; column < template.TrainIds.Count; column++)
        {
            TrainTimes.Add(new ScheduleTimeCellViewModel(template, row, column, template.TrainIds[column], reportError));
        }
    }

    public ScheduleTemplateRow Row { get; }

    public string LineId => Row.LineId;

    public string Section => Row.Section;

    public string BlockId => Row.BlockId;

    public int BlockNumber => Row.BlockNumber;

    public string Infrastructure => Row.Infrastructure;

    /// <summary>Track data, shown read-only in feet; the row stores it in meters.</summary>
    public double LengthFeet => UnitConversion.MetersToFeet(Row.LengthMeters);

    /// <summary>Track data, shown read-only in mph; the row stores it in km/h.</summary>
    public double SpeedLimitMilesPerHour => UnitConversion.KilometersPerHourToMilesPerHour(Row.SpeedLimitKilometersPerHour);

    /// <summary>A yard entry: a block a train may be selected to start from.</summary>
    public bool IsYardEntry => Row.IsYardEntry;

    public bool IsStation => Row.IsStation;

    /// <summary>One cell per train column, in column order.</summary>
    public ObservableCollection<ScheduleTimeCellViewModel> TrainTimes { get; } = new ObservableCollection<ScheduleTimeCellViewModel>();
}

/// <summary>
/// One train's cells within a <see cref="ScheduleTemplateRowViewModel"/>: the time the train
/// enters the block (its departure time in its starting block) and how long it dwells there.
/// Holds text, not TimeSpans, so blank cells (block not used by the train) are natural; times are
/// parsed when the schedule is queued. The dwell defaulting and validation rules live in
/// <see cref="ScheduleTemplateRow"/>.
/// </summary>
public class ScheduleTimeCellViewModel : ViewModelBase
{
    private readonly ScheduleTemplate _template;
    private readonly ScheduleTemplateRow _row;
    private readonly int _column;
    private readonly Action<string>? _reportError;

    /// <param name="reportError">Shows a dispatcher-readable reason an entered dwell was rejected.</param>
    public ScheduleTimeCellViewModel(ScheduleTemplate template, ScheduleTemplateRow row, int column, string trainId, Action<string>? reportError = null)
    {
        _template = template ?? throw new ArgumentNullException(nameof(template));
        _row = row;
        _column = column;
        _reportError = reportError;
        TrainId = trainId;
    }

    public string TrainId { get; }

    /// <summary>This row is the train's selected starting block: its time here is the departure time.</summary>
    public bool IsStartBlock => _row.IsRouteStartFor(_column);

    /// <summary>A starting-block change moved or affected this time; the dispatcher should check it.</summary>
    public bool NeedsReconsidering => _template.NeedsReconsidering(_row, _column);

    /// <summary>Explains a highlighted cell.</summary>
    public string? CellToolTip => NeedsReconsidering
        ? (IsStartBlock ? "Departure time from the starting block. The starting block changed: check this time." : "The starting block changed: check this arrival time.")
        : IsStartBlock ? "Departure time from this train's starting block." : null;

    /// <summary>
    /// Arrival time (departure time in the starting block). Giving a blank block a time (choosing it
    /// as a destination) fills in the block's default dwell; clearing it clears the dwell. Editing an
    /// existing time keeps it.
    /// </summary>
    public string TimeText
    {
        get => _row.TrainTimes[_column];
        set
        {
            if (_row.TrainTimes[_column] == (value ?? string.Empty))
            {
                return;
            }

            _row.SetTrainTime(_column, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DwellText));
            OnPropertyChanged(nameof(NeedsReconsidering));
            OnPropertyChanged(nameof(CellToolTip));
        }
    }

    /// <summary>
    /// Dwell time in whole seconds. Negative or non-numeric entries are rejected (the previous
    /// value stays) and reported; blank means the block's default.
    /// </summary>
    public string DwellText
    {
        get => _row.TrainDwellTimes[_column];
        set
        {
            if (_row.TrainDwellTimes[_column] == (value ?? string.Empty))
            {
                return;
            }

            if (_row.TrySetTrainDwellTime(_column, value) is { } error)
            {
                _reportError?.Invoke($"{TrainIds.DisplayName(TrainId)} at {_row.BlockId}: {error}");
            }

            // Also after a rejection, so the grid shows the value actually kept.
            OnPropertyChanged();
        }
    }

    /// <summary>Re-reads everything after the train's starting block changed.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(DwellText));
        OnPropertyChanged(nameof(IsStartBlock));
        OnPropertyChanged(nameof(NeedsReconsidering));
        OnPropertyChanged(nameof(CellToolTip));
    }
}

/// <summary>One permitted starting block in a train's selector: a yard entry, by label and actual block ID.</summary>
public sealed record StartBlockOption(string BlockId, string Label);

/// <summary>
/// One train's starting-block selector in the Schedule Builder. Offers only the line's yard
/// entries; choosing one goes through <see cref="ScheduleTemplate.SelectStartBlock"/>, which moves
/// the departure time and flags affected times.
/// </summary>
public sealed class TrainStartSelectionViewModel : ViewModelBase
{
    private readonly ScheduleTemplate _template;
    private readonly int _column;
    private readonly Action<int, StartBlockChange> _onChanged;

    public TrainStartSelectionViewModel(ScheduleTemplate template, int column, Action<int, StartBlockChange> onChanged)
    {
        _template = template ?? throw new ArgumentNullException(nameof(template));
        _column = column;
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        TrainId = template.TrainIds[column];
        Options = template.YardEntryBlockIds
            .Select(id => new StartBlockOption(id, ScheduleTemplate.Label(template.Rows.First(row => row.BlockId == id))))
            .ToList();
    }

    public string TrainId { get; }

    public string TrainName => TrainIds.DisplayName(TrainId);

    /// <summary>The line's yard entries; nothing else can be chosen.</summary>
    public IReadOnlyList<StartBlockOption> Options { get; }

    /// <summary>The selected starting block's ID; null while none is selected.</summary>
    public string? SelectedBlockId
    {
        get => _template.GetStartBlockId(_column) is { Length: > 0 } id ? id : null;
        set
        {
            if (value is null || value == _template.GetStartBlockId(_column))
            {
                return;
            }

            var change = _template.SelectStartBlock(_column, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsMissing));
            _onChanged(_column, change);
        }
    }

    /// <summary>No starting block chosen yet (a line with several yard entries); the schedule cannot be queued.</summary>
    public bool IsMissing => SelectedBlockId is null;
}
