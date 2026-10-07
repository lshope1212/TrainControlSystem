using System.Collections.ObjectModel;
using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Win32;
using TrackModel.Core.Interfaces;
using TrackModel.Core.Persistence;
using TrackModel.Core.Services;
using TrackModel.Wpf.Commands;
using TrainControl.Contracts.Messages;

namespace TrackModel.Wpf.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly ITrackService _track;
    private int _layoutRevision = -1;
    private LineOption? _selectedLine;
    private BlockViewModel? _selectedBlock;
    private string _status = "Ready";
    private string _delivery = "Waiting for external modules.";
    private int _diagramRevision;
    private string _temperatureInput = "68";
    public MainWindowViewModel(ITrackService track)
    {
        _track = track;
        ImportCommand = new RelayCommand(_ => Import());
        ExportCommand = new RelayCommand(_ => Export());
        DemoCommand = new RelayCommand(_ => LoadDemo());
        SetTemperatureCommand = new RelayCommand(_ => SetTemperature());
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
    public ICommand SetTemperatureCommand { get; }
    public string TemperatureInput { get => _temperatureInput; set => SetProperty(ref _temperatureInput, value); }
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
        set
        {
            if (SetProperty(ref _selectedBlock, value) && value is not null)
                TemperatureInput = (_track.FindBlock(value.Id)!.TemperatureCelsius * 1.8 + 32).ToString("0.##", CultureInfo.CurrentCulture);
        }
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
        Blocks.Clear(); Stations.Clear(); Failures.Clear();
        foreach (var b in _track.Layout.Blocks.Where(b => b.LineId == SelectedLine?.Id))
        {
            var adapter = new BlockViewModel(b, _track);
            Blocks.Add(adapter);
            if (b.StationName.Length > 0) Stations.Add(adapter);
            if (b.HasFailure) Failures.Add(adapter);
        }
        SelectedBlock = Blocks.FirstOrDefault(b => b.Id == previous)
            ?? Blocks.FirstOrDefault(b => b.Id == "104") ?? Blocks.FirstOrDefault();
        DiagramRevision++;
        OnPropertyChanged(nameof(DiagramHeight));
        OnPropertyChanged(nameof(BlockCount));
        OnPropertyChanged(nameof(OccupiedCount));
    }

    private void Import()
    {
        var dialog = new OpenFileDialog { Title = "Import track layout", Filter = "Track layouts (*.json;*.csv)|*.json;*.csv" };
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
        if (_track is TrackService service) { SampleTrackLayout.LoadDemo(service); Status = "Demo restored"; }
    }

    private void SetTemperature()
    {
        try
        {
            if (SelectedBlock is null) throw new ArgumentException("Select a block first.");
            if (!double.TryParse(TemperatureInput, NumberStyles.Number, CultureInfo.CurrentCulture, out var fahrenheit))
                throw new ArgumentException("Temperature must be a number in °F.");
            _track.ApplyTemperature(new TrackModelTemperatureCommandMessage
                { BlockId = SelectedBlock.Id, TemperatureCelsius = (fahrenheit - 32) / 1.8 });
            Status = "Temperature updated for block " + SelectedBlock.Id;
        }
        catch (ArgumentException ex) { Status = "Rejected: " + ex.Message; }
    }
}
