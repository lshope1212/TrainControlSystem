using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using TrackController.Contracts.Messages;
using TrackController.Core.Services;
using TrackController.Runtime;
using TrackController.Wpf.Commands;
using TrackController.Wpf.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Wpf.ViewModels;

/// <summary>Binds the operator window to the running controller and its deployment.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly TrackControllerService _controller;
    private WaysidePipeHost _host;
    private readonly WaysideEndpoints _endpoints;
    private readonly WaysidePipeClient _sender;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private static readonly JsonSerializerOptions ConfigurationJson = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;
    private string _status = "Open Controller setup to load a layout and choose PLC or software logic.";
    private string _connection = "Starting local connection…";
    private string _selectedBlockId = "5";
    private WaysideSnapshotMessage _snapshot = null!;
    private IReadOnlyList<MovementRow> _movements = [];
    private IReadOnlyList<EquipmentRow> _equipment = [];
    private string[] _blockIds = [], _switchIds = [], _signalIds = [], _crossingIds = [];
    private bool _disposed;
    private string _plcSource = "Select a .plc file to view its Boolean program.";
    private bool _hardwareMode = true;
    private bool _maintenanceMode;
    private int _speedMultiplier = 1;
    private int _tabIndex;
    private string? _deploymentPath;
    private string _deploymentDescription = "Local PC preview · all blocks";
    private string _programSource = "Select a .plc file to view its Boolean program.";

    /// <summary>Creates the operator view model around the single running controller.</summary>
    public MainWindowViewModel(TrackControllerService controller, WaysideEndpoints? endpoints = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _dispatcher = Dispatcher.CurrentDispatcher;
        _endpoints = endpoints ?? WaysideEndpoints.Live;
        _sender = new WaysidePipeClient(_endpoints.Input);
        _host = new WaysidePipeHost(_controller, _endpoints);
        ObserveHost();
        PauseResumeCommand = Command(() => SendAsync(new WaysideSimulationMessage { IsPaused = !Snapshot.IsPaused, SpeedMultiplier = SpeedMultiplier }));
        LoadPlcCommand = Command(ChoosePlcAsync);
        LoadSamplePlcCommand = Command(() => LoadPlcFileAsync(Path.Combine(AppContext.BaseDirectory, "Samples", "wayside.plc")));
        LoadLayoutCommand = Command(ChooseLayoutAsync);
        LoadDeploymentCommand = Command(ChooseDeploymentAsync);
        LoadSampleDeploymentCommand = Command(() => ReplaceDeploymentAsync(Path.Combine(AppContext.BaseDirectory, "Samples", "all-pc.deployment.json")));
        ApplyExecutionCommand = Command(ApplyExecutionAsync);
        SaveExecutionCommand = Command(SaveExecutionAsync);
        RestoreLocalDemoCommand = Command(RestoreLocalDemoAsync);
        ResetControllerCommand = Command(ResetControllerAsync);
        SendManualCommand = Command(SendManualAsync);
        Refresh();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Refresh(), _dispatcher);
        _host.Start();
    }

    public ICommand PauseResumeCommand { get; }

    public ICommand LoadPlcCommand { get; }

    public ICommand LoadSamplePlcCommand { get; }

    public ICommand LoadLayoutCommand { get; }

    public ICommand LoadDeploymentCommand { get; }

    public ICommand LoadSampleDeploymentCommand { get; }

    public ICommand ApplyExecutionCommand { get; }

    public ICommand SaveExecutionCommand { get; }

    public ICommand RestoreLocalDemoCommand { get; }

    public ICommand SendManualCommand { get; }

    private AsyncRelayCommand Command(Func<Task> action) => new(() => RunAsync(action), () => !_disposed);

    public string[] ControllerModes { get; } = ["PLC program", "Software logic"];

    public string ControllerMode
    {
        get => IsHardwareMode ? ControllerModes[0] : ControllerModes[1];
        set => IsHardwareMode = value == ControllerModes[0];
    }

    public bool CanChooseLogic => !IsRoutedDeployment;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string PlcSource
    {
        get => _plcSource;
        private set => SetProperty(ref _plcSource, value);
    }

    public string PlcSummary => Snapshot.IsHardwareMode ? Snapshot.PlcStatus.Replace("hardware mode", "PLC logic") : "Software logic active. A PLC program is not required.";

    public WaysideSnapshotMessage Snapshot => _snapshot;

    public IReadOnlyList<WaysideBlockStatus> Blocks => _snapshot.Blocks;

    public IReadOnlyList<MovementRow> Movements => _movements;

    public IReadOnlyList<EquipmentRow> Equipment => _equipment;

    public string[] BlockIds => _blockIds;

    public string[] SwitchIds => _switchIds;

    public string[] SignalIds => _signalIds;

    public string[] CrossingIds => _crossingIds;

    public string SelectedBlockId
    {
        get => _selectedBlockId;
        set
        {
            if (SetProperty(ref _selectedBlockId, value))
            {
                NotifySelection();
            }
        }
    }

    public WaysideBlockStatus? SelectedBlock => Blocks.FirstOrDefault(blockStatus => blockStatus.BlockId == SelectedBlockId);

    public MovementRow? SelectedMovement => Movements.FirstOrDefault(movement => movement.Target == SelectedBlockId);

    public string SelectedLimit => SelectedBlock is { } blockStatus ? $"Track speed limit: {DisplayUnits.FormatMph(blockStatus.SpeedLimitMetersPerSecond)} mph" : "";

    public string ActualSpeed => SelectedBlock?.ActualSpeedMetersPerSecond is double speed ? $"{DisplayUnits.FormatMph(speed)} mph" : "Unavailable";

    public string SimulationStatus => _snapshot.IsPaused ? "Paused" : $"Running · {_snapshot.SpeedMultiplier:0}×";

    public string SimulationTime => TimeSpan.FromSeconds(_snapshot.SimulationSeconds).ToString(@"hh\:mm\:ss");

    public string PauseResumeLabel => _snapshot.IsPaused ? "Resume" : "Pause";

    public int TabIndex
    {
        get => _tabIndex;
        set => SetProperty(ref _tabIndex, value);
    }

    public bool IsHardwareMode
    {
        get => _hardwareMode;
        set
        {
            if (SetProperty(ref _hardwareMode, value))
            {
                _ = RunAsync(() => SendAsync(new WaysideControllerModeMessage { IsHardwareMode = value }));
            }
        }
    }

    public bool IsMaintenanceMode
    {
        get => _maintenanceMode;
        set
        {
            if (SetProperty(ref _maintenanceMode, value))
            {
                _ = RunAsync(() => SendAsync(new WaysideMaintenanceModeMessage { IsEnabled = value }));
            }
        }
    }

    public int SpeedMultiplier
    {
        get => _speedMultiplier;
        set
        {
            if (SetProperty(ref _speedMultiplier, value))
            {
                _ = RunAsync(() => SendAsync(new WaysideSimulationMessage { IsPaused = _snapshot.IsPaused, SpeedMultiplier = value }));
            }
        }
    }

    public int[] SpeedMultipliers { get; } = Enumerable.Range(1, 10).ToArray();

    private void NotifySelection()
    {
        foreach (string propertyName in new[]
        {
            nameof(SelectedBlock),
            nameof(SelectedMovement),
            nameof(SelectedLimit),
            nameof(ActualSpeed),
            nameof(SelectedExecutionTarget)
        }

        )
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void RefreshBlockChoices()
    {
        // Choices describe configuration, not feedback freshness. Keep them while awaiting a new
        // deployment's first output, and avoid resetting WPF selectors for every network snapshot.
        if (Blocks.Count == 0)
        {
            return;
        }

        Update(ref _blockIds, Blocks.Select(blockStatus => blockStatus.BlockId).ToArray(), nameof(BlockIds));
        Update(ref _switchIds, Blocks.Where(blockStatus => blockStatus.HasSwitch).Select(blockStatus => blockStatus.BlockId).ToArray(), nameof(SwitchIds));
        Update(ref _signalIds, Blocks.Where(blockStatus => blockStatus.HasSignal).Select(blockStatus => blockStatus.BlockId).ToArray(), nameof(SignalIds));
        Update(ref _crossingIds, Blocks.Where(blockStatus => blockStatus.HasCrossing).Select(blockStatus => blockStatus.BlockId).ToArray(), nameof(CrossingIds));
        void Update(ref string[] current, string[] next, string property)
        {
            if (current.SequenceEqual(next))
            {
                return;
            }

            current = next;
            OnPropertyChanged(property);
        }
    }

    private void Post(Action action)
    {
        if (!_disposed)
        {
            _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed)
            {
                action();
            }
        });
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_disposed)
        {
            return;
        }

        await _operations.WaitAsync();
        try
        {
            if (!_disposed)
            {
                await action();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or TimeoutException or OperationCanceledException)
        {
            if (!_disposed)
            {
                Status = ex.Message;
            }
        }
        finally
        {
            if (!_disposed)
            {
                Refresh();
            }

            _operations.Release();
        }
    }

    private async Task ChoosePlcAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load Boolean PLC program",
            Filter = "PLC programs (*.plc;*.txt)|*.plc;*.txt|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            await LoadPlcFileAsync(dialog.FileName);
        }
    }

    public ICommand ResetControllerCommand { get; }

    public bool IsRoutedDeployment => _deploymentPath is not null;

    public string DeploymentDescription => _deploymentDescription;

    public string Connection
    {
        get => _connection;
        private set => SetProperty(ref _connection, value);
    }

    public string SelectedExecutionTarget => _host.GetOwnerDescription(SelectedBlockId);

    public string DataStatus => $"Track feedback: {Blocks.Count(block => block.IsFresh)}/{Blocks.Count} current · Logic: {(Snapshot.IsHardwareMode ? "PLC program" : "Software")} · {Snapshot.Authorizations.Count} movement request(s)";

    public string LastResult => Movements.LastOrDefault() is { } movement ? $"{movement.Status} — {movement.AuthorizedSpeed} mph / {movement.AuthorizedAuthority} ft\n{movement.Reason}" : "No movement request submitted.";

    private void Refresh()
    {
        _snapshot = _host.LatestSnapshot;
        _hardwareMode = _snapshot.IsHardwareMode;
        _maintenanceMode = _snapshot.IsMaintenanceMode;
        _speedMultiplier = (int)_snapshot.SpeedMultiplier;
        OnPropertyChanged(nameof(IsHardwareMode));
        OnPropertyChanged(nameof(ControllerMode));
        OnPropertyChanged(nameof(IsMaintenanceMode));
        OnPropertyChanged(nameof(SpeedMultiplier));

        _movements = _snapshot.Authorizations.Select(authorization => new MovementRow(authorization)).ToArray();
        _equipment = Blocks.SelectMany(blockStatus =>
        {
            var rows = new List<EquipmentRow>();
            string status = blockStatus.Status;
            if (blockStatus.HasSwitch)
            {
                rows.Add(new($"Switch {blockStatus.BlockId}", blockStatus.BlockId, blockStatus.CommandedSwitch.ToString(), blockStatus.ReportedSwitch.ToString(), status));
            }

            if (blockStatus.HasSignal)
            {
                rows.Add(new($"Signal {blockStatus.BlockId}", blockStatus.BlockId, blockStatus.CommandedSignal.ToString(), blockStatus.ReportedSignal.ToString(), status));
            }

            if (blockStatus.HasCrossing)
            {
                rows.Add(new($"Crossing {blockStatus.BlockId}", blockStatus.BlockId, blockStatus.CommandedCrossing.ToString(), blockStatus.ReportedCrossing.ToString(), status));
            }

            return rows;
        }).ToArray();
        foreach (string propertyName in new[]
        {
            nameof(Snapshot),
            nameof(PlcSummary),
            nameof(Blocks),
            nameof(Movements),
            nameof(Equipment),
            nameof(SimulationStatus),
            nameof(SimulationTime),
            nameof(PauseResumeLabel),
            nameof(DataStatus),
            nameof(LastResult),
            nameof(Connection)
        }

        )
        {
            OnPropertyChanged(propertyName);
        }

        RefreshBlockChoices();

        NotifySelection();
        OnPropertyChanged(nameof(CanEditExecution));
        OnPropertyChanged(nameof(CanApplyExecution));
    }

    private async Task SendAsync(object message, bool announce = true)
    {
        await _sender.SendAsync(message, _lifetime.Token);

        if (announce)
        {
            Status = message switch
            {
                WaysideFeedbackMessage => "Track feedback sent. Waiting for the controller response.",
                WaysideMovementRequestMessage request => request.IsActive ? "Movement request sent. Check Controller response for the decision." : "Movement cancellation sent.",
                WaysideSimulationMessage simulation => simulation.IsPaused ? "Pause requested." : "Resume requested. Check the simulation status above.",
                SwitchPositionRequestMessage => "Switch request sent. Report the actual switch position separately.",
                MaintenanceRequestMessage => "Block closure request sent. Check the block status for the result.",
                WaysideManualCommandMessage => "Manual command sent. Equipment feedback must confirm the change.",
                WaysideMaintenanceModeMessage => "Maintenance mode change sent.",
                WaysideControllerModeMessage => "Controller logic change sent.",
                WaysidePlcProgramMessage => "PLC program sent. Check Controller setup for its status before resuming.",
                _ => "Input sent. Waiting for the controller response."
            };
        }
    }

    private async Task LoadPlcFileAsync(string path)
    {
        await SendAsync(new WaysideSimulationMessage { IsPaused = true, SpeedMultiplier = Snapshot.SpeedMultiplier });
        if (new FileInfo(path).Length > 65536)
        {
            throw new ArgumentException("PLC file exceeds 64 KiB.");
        }

        string source = File.ReadAllText(path);
        PlcSource = source;
        _programSource = source;
        _programName = Path.GetFileName(path);
        await SendAsync(new WaysidePlcProgramMessage { Name = Path.GetFileName(path), Source = source });
    }

    private async Task ChooseLayoutAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load wayside territory",
            Filter = "Territory configuration (*.json)|*.json"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (new FileInfo(dialog.FileName).Length > 1048576)
        {
            throw new ArgumentException("Territory file exceeds 1 MiB.");
        }

        var config = JsonSerializer.Deserialize<WaysideConfigurationMessage>(File.ReadAllText(dialog.FileName), ConfigurationJson) ?? throw new ArgumentException("Empty configuration.");
        await SendAsync(config);
        SelectedBlockId = config.Blocks.FirstOrDefault()?.BlockId ?? "";
        Status = "Territory configuration sent. Check the controller response before resuming.";
    }

    /// <summary>Stops UI updates and awaits the controller host before shutdown.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        await _lifetime.CancelAsync();
        await _operations.WaitAsync();
        try
        {
            await _host.DisposeAsync();
        }
        finally
        {
            _operations.Release();
        }
    }

}
