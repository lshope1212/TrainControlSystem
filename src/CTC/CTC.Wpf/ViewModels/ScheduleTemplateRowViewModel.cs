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
            TrainTimes.Add(new ScheduleTimeCellViewModel(row, column, template.TrainIds[column], reportError));
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

    public bool IsStartBlock => Row.IsRouteStart;

    public bool IsStation => Row.IsStation;

    /// <summary>One cell per train column, in column order.</summary>
    public ObservableCollection<ScheduleTimeCellViewModel> TrainTimes { get; } = new ObservableCollection<ScheduleTimeCellViewModel>();
}

/// <summary>
/// One train's cells within a <see cref="ScheduleTemplateRowViewModel"/>: the time the train
/// enters the block and how long it dwells there. Holds text, not TimeSpans, so blank cells
/// (block not used by the train) are natural; times are parsed when the schedule is queued.
/// The dwell defaulting and validation rules live in <see cref="ScheduleTemplateRow"/>.
/// </summary>
public class ScheduleTimeCellViewModel : ViewModelBase
{
    private readonly ScheduleTemplateRow _row;
    private readonly int _column;
    private readonly Action<string>? _reportError;

    /// <param name="reportError">Shows a dispatcher-readable reason an entered dwell was rejected.</param>
    public ScheduleTimeCellViewModel(ScheduleTemplateRow row, int column, string trainId, Action<string>? reportError = null)
    {
        _row = row;
        _column = column;
        _reportError = reportError;
        TrainId = trainId;
    }

    public string TrainId { get; }

    /// <summary>
    /// Arrival time. Giving a blank block a time (choosing it as a destination) fills in the
    /// block's default dwell; clearing it clears the dwell. Editing an existing time keeps it.
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
}
