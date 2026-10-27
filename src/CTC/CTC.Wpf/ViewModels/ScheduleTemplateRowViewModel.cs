using System.Collections.ObjectModel;
using CTC.Core.Scheduling;
using TrainControl.Common.Utilities;

namespace CTC.Wpf.ViewModels;

/// <summary>
/// One block's row in the Schedule Builder grid. Wraps a <see cref="ScheduleTemplateRow"/>;
/// the train time cells write straight through to it.
/// </summary>
public class ScheduleTemplateRowViewModel
{
    public ScheduleTemplateRowViewModel(ScheduleTemplate template, ScheduleTemplateRow row)
    {
        ArgumentNullException.ThrowIfNull(template);
        Row = row ?? throw new ArgumentNullException(nameof(row));

        for (int column = 0; column < template.TrainIds.Count; column++)
        {
            TrainTimes.Add(new ScheduleTimeCellViewModel(row, column, template.TrainIds[column]));
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
/// One train's time cell within a <see cref="ScheduleTemplateRowViewModel"/>: the time the
/// train enters the block. Holds text, not a TimeSpan, so blank cells (block not used by the
/// train) are natural; parsing happens when the schedule is queued.
/// </summary>
public class ScheduleTimeCellViewModel : ViewModelBase
{
    private readonly ScheduleTemplateRow _row;
    private readonly int _column;

    public ScheduleTimeCellViewModel(ScheduleTemplateRow row, int column, string trainId)
    {
        _row = row;
        _column = column;
        TrainId = trainId;
    }

    public string TrainId { get; }

    public string TimeText
    {
        get => _row.TrainTimes[_column];
        set
        {
            if (_row.TrainTimes[_column] == (value ?? string.Empty))
            {
                return;
            }

            _row.TrainTimes[_column] = value ?? string.Empty;
            OnPropertyChanged();
        }
    }
}
