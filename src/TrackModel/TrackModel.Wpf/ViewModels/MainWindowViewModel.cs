using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Win32;
using TrackModel.Core.Interfaces;
using TrackModel.Core.Persistence;
using TrackModel.Core.Services;
using TrackModel.Wpf.Commands;

namespace TrackModel.Wpf.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly ITrackService _track;
    private int _layoutRevision = -1;
    private LineOption? _selectedLine;
    private BlockViewModel? _selectedBlock;
    private BlockViewModel? _selectedStation;
    private string _arrivingCustomers = "10";
    private string _status = "Ready";
    private string _delivery = "Waiting for external modules.";
    private int _diagramRevision;
    public MainWindowViewModel(ITrackService track)
    {
        _track = track;
        ImportCommand = new RelayCommand(_ => Import());
        ExportCommand = new RelayCommand(_ => Export());
        DemoCommand = new RelayCommand(_ => LoadDemo());
        RecordArrivalsCommand = new RelayCommand(_ => RecordArrivals());
        _track.StateChanged += (_, _) => Refresh();
        Refresh();
    }
    public ObservableCollection<LineOption> Lines { get; } = [];
    public ObservableCollection<BlockViewModel> Blocks { get; } = [];
    public ObservableCollection<BlockViewModel> Stations { get; } = [];
    public ObservableCollection<BlockViewModel> Failures { get; } = [];
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand DemoCommand { get; }
    public ICommand RecordArrivalsCommand { get; }
    public BlockViewModel? SelectedStation { get => _selectedStation; set => SetProperty(ref _selectedStation, value); }
    public string ArrivingCustomers { get => _arrivingCustomers; set => SetProperty(ref _arrivingCustomers, value); }
    public string LayoutName => _track.Layout.Name;
    public string SystemTime => _track.SystemTime.ToString(@"hh\:mm\:ss");
    public int BlockCount => Blocks.Count;
    public int OccupiedCount => Blocks.Count(b => b.IsOccupied);
    public double DiagramHeight => Blocks.Any(b => b.Section == "Return") ? 730 : Math.Max(730, Math.Ceiling(Blocks.Count / 8d) * 150 + 270);
    public int DiagramRevision { get => _diagramRevision; private set => SetProperty(ref _diagramRevision, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public string Delivery { get => _delivery; set => SetProperty(ref _delivery, value); }
    public LineOption? SelectedLine
    {
        get => _selectedLine;
        set { if (SetProperty(ref _selectedLine, value)) RebuildBlocks(); }
    }
    public BlockViewModel? SelectedBlock
    {
        get => _selectedBlock;
        set => SetProperty(ref _selectedBlock, value);
    }

    private void Refresh()
    {
        if (_layoutRevision != _track.LayoutRevision)
        {
            _layoutRevision = _track.LayoutRevision;
            var previous = SelectedLine?.Id;
            Lines.Clear();
            foreach (var line in _track.Layout.Blocks.Select(b => b.LineId).Distinct()) Lines.Add(new(line));
            _selectedLine = Lines.FirstOrDefault(l => l.Id == previous) ?? Lines.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedLine));
            RebuildBlocks();
        }
        foreach (var b in Blocks) b.Refresh();
        Failures.Clear();
        foreach (var b in Blocks.Where(b => b.HasFailure)) Failures.Add(b);
        DiagramRevision++;
        OnPropertyChanged(nameof(LayoutName));
        OnPropertyChanged(nameof(SystemTime));
        OnPropertyChanged(nameof(BlockCount));
        OnPropertyChanged(nameof(OccupiedCount));
        OnPropertyChanged(nameof(DiagramHeight));
    }

    private void RebuildBlocks()
    {
        var previous = SelectedBlock?.Id;
        var previousStation = SelectedStation?.Id;
        Blocks.Clear(); Stations.Clear(); Failures.Clear();
        foreach (var b in _track.Layout.Blocks.Where(b => b.LineId == SelectedLine?.Id))
        {
            var adapter = new BlockViewModel(b, _track);
            Blocks.Add(adapter);
            if (b.StationName.Length > 0) Stations.Add(adapter);
            if (b.HasFailure) Failures.Add(adapter);
        }
        SelectedBlock = Blocks.FirstOrDefault(b => b.Id == previous)
            ?? Blocks.FirstOrDefault();
        SelectedStation = Stations.FirstOrDefault(b => b.Id == previousStation) ?? Stations.FirstOrDefault();
        DiagramRevision++;
        OnPropertyChanged(nameof(DiagramHeight));
        OnPropertyChanged(nameof(BlockCount));
        OnPropertyChanged(nameof(OccupiedCount));
    }

    private void RecordArrivals()
    {
        if (SelectedStation is null || !int.TryParse(ArrivingCustomers, out var count) || count <= 0)
        {
            Status = "Select a station and enter a positive whole number of arriving customers.";
            return;
        }
        try
        {
            _track.RecordStationArrivals(SelectedStation.Id, count);
            Status = $"{count} customers entered {SelectedStation.Station}; {count} tickets sold.";
        }
        catch (ArgumentException ex) { Status = ex.Message; }
    }

    private void Import()
    {
        var dialog = new OpenFileDialog { Title = "Import track layout (XLSX imports the course vF5 Blue, Red and Green Lines)", Filter = "Track layouts (*.json;*.csv;*.xlsx)|*.json;*.csv;*.xlsx" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            _track.LoadLayout(new TrackFileRepository().Load(dialog.FileName));
            Status = "Imported " + Path.GetFileName(dialog.FileName);
        }
        catch (Exception ex) { Status = "Import failed: " + ex.Message; }
    }

    private void Export()
    {
        var dialog = new SaveFileDialog { Title = "Export static track layout", Filter = "JSON layout (*.json)|*.json", FileName = "track-layout.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(_track.Layout,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            Status = "Layout exported to " + dialog.FileName;
        }
        catch (Exception ex) { Status = "Export failed: " + ex.Message; }
    }

    private void LoadDemo()
    {
        // Restores the production start-up state (no trains), never a demonstration train.
        if (_track is TrackService service) { CourseTrackLayout.Load(service); Status = "Course Blue, Red and Green Lines restored (no trains)"; }
    }

}
