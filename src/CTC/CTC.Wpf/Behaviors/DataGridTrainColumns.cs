using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CTC.Core.Models;

namespace CTC.Wpf.Behaviors;

/// <summary>
/// Attached behavior that appends one time column per train to a schedule DataGrid, since
/// the number of trains is only known at run time. Column N binds to
/// <c>TrainTimes[N].TimeText</c> on each row. Presentation only: no schedule logic here.
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

    // Every block row accepts a time, so all cells are editable.
    private static DataGridColumn CreateTrainColumn(string trainId, int column) => new DataGridTextColumn
    {
        Header = $"{TrainIds.DisplayName(trainId)} Arrival Time",
        Width = new DataGridLength(150),
        Binding = new Binding($"TrainTimes[{column}].TimeText")
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
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
                var column = CreateTrainColumn(trainId, index++);
                _addedColumns.Add(column);
                _grid.Columns.Add(column);
            }
        }
    }
}
