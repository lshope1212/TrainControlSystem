using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using CTC.Core.Models;

namespace CTC.Wpf.Behaviors;

/// <summary>
/// Attached behavior that appends an Arrival Time and a Dwell (sec) column per train to a
/// schedule DataGrid, since the number of trains is only known at run time. Train N's columns
/// bind to <c>TrainTimes[N].TimeText</c> and <c>TrainTimes[N].DwellText</c> on each row; the time
/// cell in the train's starting block (its departure time) is highlighted.
/// Presentation only: no schedule logic here.
/// </summary>
/// <example>
/// <c>&lt;DataGrid behaviors:DataGridTrainColumns.Source="{Binding TrainColumns}" /&gt;</c>
/// </example>
public static class DataGridTrainColumns
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source",
        typeof(IEnumerable<string>),
        typeof(DataGridTrainColumns),
        new PropertyMetadata(null, OnSourceChanged));

    // Per-grid bookkeeping: the columns this behavior added and its collection handler.
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(ColumnState),
        typeof(DataGridTrainColumns));

    public static IEnumerable<string>? GetSource(DependencyObject element) => (IEnumerable<string>?)element.GetValue(SourceProperty);

    public static void SetSource(DependencyObject element, IEnumerable<string>? value) => element.SetValue(SourceProperty, value);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid)
        {
            return;
        }

        var state = (ColumnState?)grid.GetValue(StateProperty);
        if (state is null)
        {
            state = new ColumnState(grid);
            grid.SetValue(StateProperty, state);
        }

        state.Attach(e.NewValue as IEnumerable<string>);
    }

    private static readonly Brush StartBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7)));
    private static readonly Brush ReconsiderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFE, 0xF3, 0xC7)));

    // Every block row accepts a time, so all cells are editable. The train's starting block is
    // highlighted (its time there is the departure time); a time a starting-block change affected
    // is highlighted until it is edited.
    private static DataGridColumn CreateTrainColumn(string trainId, int column) => new DataGridTextColumn
    {
        Header = $"{TrainIds.DisplayName(trainId)} Arrival Time",
        Width = new DataGridLength(150),
        Binding = new Binding($"TrainTimes[{column}].TimeText")
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        },
        CellStyle = CreateTimeCellStyle(column),
    };

    private static Style CreateTimeCellStyle(int column)
    {
        var style = new Style(typeof(DataGridCell));
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding($"TrainTimes[{column}].CellToolTip")));

        var start = new DataTrigger { Binding = new Binding($"TrainTimes[{column}].IsStartBlock"), Value = true };
        start.Setters.Add(new Setter(Control.BackgroundProperty, StartBrush));
        start.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Triggers.Add(start);

        // Added after the start trigger, so a start cell that needs reconsidering shows the warning.
        var reconsider = new DataTrigger { Binding = new Binding($"TrainTimes[{column}].NeedsReconsidering"), Value = true };
        reconsider.Setters.Add(new Setter(Control.BackgroundProperty, ReconsiderBrush));
        style.Triggers.Add(reconsider);
        return style;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    // Filled with the block's default when the train is given a time there; committed when the
    // cell is left, so a value is validated whole rather than keystroke by keystroke.
    private static DataGridColumn CreateDwellColumn(string trainId, int column) => new DataGridTextColumn
    {
        Header = $"{TrainIds.DisplayName(trainId)} Dwell (sec)",
        Width = new DataGridLength(130),
        Binding = new Binding($"TrainTimes[{column}].DwellText")
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
        },
    };

    private sealed class ColumnState
    {
        private readonly DataGrid _grid;
        private readonly List<DataGridColumn> _addedColumns = new List<DataGridColumn>();
        private IEnumerable<string>? _source;

        public ColumnState(DataGrid grid) => _grid = grid;

        public void Attach(IEnumerable<string>? source)
        {
            if (_source is INotifyCollectionChanged oldObservable)
            {
                oldObservable.CollectionChanged -= OnCollectionChanged;
            }

            _source = source;

            if (_source is INotifyCollectionChanged newObservable)
            {
                newObservable.CollectionChanged += OnCollectionChanged;
            }

            Rebuild();
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

        private void Rebuild()
        {
            foreach (var column in _addedColumns)
            {
                _grid.Columns.Remove(column);
            }

            _addedColumns.Clear();

            if (_source is null)
            {
                return;
            }

            int index = 0;
            foreach (var trainId in _source)
            {
                foreach (var column in new[] { CreateTrainColumn(trainId, index), CreateDwellColumn(trainId, index) })
                {
                    _addedColumns.Add(column);
                    _grid.Columns.Add(column);
                }

                index++;
            }
        }
    }
}
