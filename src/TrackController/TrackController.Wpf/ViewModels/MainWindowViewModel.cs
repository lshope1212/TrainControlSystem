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
using TrainControl.Common.Communication;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Wpf.ViewModels;

/// <summary>Binds the operator window to the running controller and its deployment.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly TrackControllerService _controller;
    private WaysidePipeHost _host;
    private readonly string[] _pipeNames;
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
    private string _status = "Open Controller setup, choose a line, then select Load setup.";
    private string _connection = "Starting local connection…";
    private string _selectedBlockId = "5";
    private WaysideSnapshotMessage _snapshot = null!;
    private IReadOnlyList<MovementRow> _movements = [];
    private IReadOnlyList<EquipmentRow> _equipment = [];
    private string[] _blockIds = [];
    private bool _disposed;
    private string _plcSource = "Select a .plc file to view its Boolean program.";
    private bool _hardwareMode = true;
    private bool _maintenanceMode;
    private int _speedMultiplier = 1;
    private int _tabIndex;

    /// <summary>Creates the operator view model around the single running controller.</summary>
    public MainWindowViewModel(TrackControllerService controller, string[]? pipeNames = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _dispatcher = Dispatcher.CurrentDispatcher;
        _pipeNames = pipeNames is null ? WaysidePipeHost.CreatePipeNames() : [.. pipeNames];
        _host = new WaysidePipeHost(_controller, _pipeNames);
        ObserveHost();
        PauseResumeCommand = Command(() => SendAsync(MessageSerializer.CreateEnvelope("SetSimulation", new { paused = !Snapshot.IsPaused, speedMultiplier = SpeedMultiplier })));
        LoadPlcCommand = new AsyncRelayCommand(() => LoadSelectedPlcAsync(), () => !_disposed);
        LoadLayoutCommand = Command(ChooseLayoutAsync);
        LoadSetupCommand = new AsyncRelayCommand(() => RunAsync(LoadSelectedSetupAsync), () => !_disposed && Snapshot is { IsPaused: true });
        ApplyExecutionCommand = Command(ApplyExecutionAsync);
        SaveExecutionCommand = Command(SaveExecutionAsync);
        RestoreLocalDemoCommand = Command(() => UseDeploymentAsync(null));
        ResetControllerCommand = Command(ResetControllerAsync);
        SendManualCommand = Command(SendManualAsync);
        Refresh();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Refresh(), _dispatcher);
        _host.Start();
    }

    public ICommand PauseResumeCommand { get; }

    public ICommand LoadPlcCommand { get; }

    public ICommand LoadLayoutCommand { get; }

    public ICommand LoadSetupCommand { get; }

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

    public System.Windows.Media.Brush LineAccentBrush => LineAccent.For(_snapshot.LineName);

    public IReadOnlyList<WaysideBlockStatus> Blocks => _snapshot.Blocks;

    public IReadOnlyList<MovementRow> Movements => _movements;

    public IReadOnlyList<EquipmentRow> Equipment => _equipment;

    public string[] BlockIds => _blockIds;

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
                _ = RunAsync(() => SendAsync(MessageSerializer.CreateEnvelope("SetControllerMode", new { hardwareMode = value })));
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
                _ = RunAsync(() => SendAsync(MessageSerializer.CreateEnvelope("SetMaintenanceMode", new { enabled = value })));
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
                _ = RunAsync(() => SendAsync(MessageSerializer.CreateEnvelope("SetSimulation", new { paused = _snapshot.IsPaused, speedMultiplier = value })));
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

    public ICommand ResetControllerCommand { get; }

    public bool IsRoutedDeployment => _plan is not null;

    public string Connection
    {
        get => _connection;
        private set => SetProperty(ref _connection, value);
    }

    public string SelectedExecutionTarget => _host.GetOwnerDescription(SelectedBlockId);

    public string DataStatus => $"Track feedback: {Blocks.Count(block => block.IsFresh)}/{Blocks.Count} current · Logic: {(Snapshot.IsHardwareMode ? "PLC program" : "Software")} · {Snapshot.Authorizations.Count} movement request(s)";

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
                string switchStatus = blockStatus.IsFresh && blockStatus.CommandedSwitch != blockStatus.ReportedSwitch ? $"Waiting for {blockStatus.CommandedSwitch} feedback" : status;
                rows.Add(new($"Switch {blockStatus.BlockId}", blockStatus.BlockId, blockStatus.CommandedSwitch.ToString(), blockStatus.ReportedSwitch.ToString(), switchStatus));
            }

            if (blockStatus.HasSignal)
            {
                rows.Add(new($"Signal {blockStatus.BlockId}", blockStatus.BlockId, blockStatus.CommandedSignal.ToString().Replace("SuperGreen", "Super Green"), blockStatus.ReportedSignal.ToString().Replace("SuperGreen", "Super Green"), status));
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
            nameof(LineAccentBrush),
            nameof(PlcSummary),
            nameof(Blocks),
            nameof(Movements),
            nameof(Equipment),
            nameof(SimulationStatus),
            nameof(SimulationTime),
            nameof(PauseResumeLabel),
            nameof(DataStatus),
            nameof(Connection)
        }

        )
        {
            OnPropertyChanged(propertyName);
        }

        // Keep selections during missing feedback, and replace the choice list only when the layout changes.
        var blockIds = Blocks.Select(block => block.BlockId).ToArray();
        if (blockIds.Length > 0 && !_blockIds.SequenceEqual(blockIds))
        {
            _blockIds = blockIds;
            OnPropertyChanged(nameof(BlockIds));
            if (!_blockIds.Contains(SelectedBlockId))
            {
                SelectedBlockId = _blockIds[0];
            }
            ManualTarget = Blocks.FirstOrDefault(block => block.HasSwitch)?.BlockId
                ?? (_blockIds.Contains(ManualTarget) ? ManualTarget : _blockIds[0]);
        }

        NotifySelection();
        OnPropertyChanged(nameof(CanEditExecution));
        OnPropertyChanged(nameof(CanApplyExecution));
    }

    private async Task SendAsync(object message, bool announce = true)
    {
        await NamedPipeTransport.SendAsync(_pipeNames[(int)WaysidePipeHost.Pipe.Input], message, _lifetime.Token);

        if (announce)
        {
            Status = message switch
            {
                IReadOnlyList<TrackModelBlockStateMessage> => "Track feedback sent. Waiting for the controller response.",
                MovementSuggestionMessage request => request.IsActive ? "Movement request sent. Check Controller response for the decision." : "Movement cancellation sent.",
                MessageEnvelope { MessageType: "SetSimulation" } simulation => MessageSerializer.ReadValue<bool>(simulation, "paused") ? "Pause requested." : "Resume requested. Check the simulation status above.",
                SwitchPositionRequestMessage => "Switch request sent. Report the actual switch position separately.",
                MaintenanceRequestMessage => "Block closure request sent. Check the block status for the result.",
                MessageEnvelope { MessageType: "ApplyManual" } => "Manual command sent. Equipment feedback must confirm the change.",
                MessageEnvelope { MessageType: "SetMaintenanceMode" } => "Maintenance mode change sent.",
                MessageEnvelope { MessageType: "SetControllerMode" } => "Controller logic change sent.",
                MessageEnvelope { MessageType: "LoadPlc" } => "PLC program sent. Check Controller setup for its status before resuming.",
                _ => "Input sent. Waiting for the controller response."
            };
        }
    }

    /// <summary>Loads a PLC for the selected territory, prompting for a file when no path is supplied.</summary>
    public Task LoadSelectedPlcAsync(string? path = null) => RunAsync(async () =>
    {
        if (path is null)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Load Boolean PLC program",
                Filter = "PLC programs (*.plc;*.txt)|*.plc;*.txt|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }
            path = dialog.FileName;
        }

        if (new FileInfo(path).Length > 262144)
        {
            throw new ArgumentException("PLC file exceeds 256 KiB.");
        }

        string source = File.ReadAllText(path);
        if (_plan is { } plan)
        {
            var selected = SelectedPlcTerritory ?? throw new InvalidOperationException("Select a PLC territory first.");
            if (!(await _host.PollForSetupAsync(_lifetime.Token)).IsPaused)
            {
                throw new InvalidOperationException("Pause the controller before assigning a PLC.");
            }
            string id = selected.Id;
            var replacement = plan.WithTerritoryProgram(id, Path.GetFileName(path), source);
            await SendAsync(MessageSerializer.CreateEnvelope("LoadPlc", new { territoryId = id, name = Path.GetFileName(path), source = source }));
            _plan = replacement;
            var displayed = replacement.Territories.SingleOrDefault(territory => territory.Id == SelectedPlcTerritory?.Id);
            PlcSource = displayed?.ProgramSource ?? replacement.ProgramSource;
            OnPropertyChanged(nameof(SelectedPlcName));
            Status = $"PLC sent to {selected.Name}. Check PLC status before resuming; Save applied choices preserves the assignment.";
        }
        else
        {
            PlcSource = source;
            _programName = Path.GetFileName(path);
            await SendAsync(MessageSerializer.CreateEnvelope("LoadPlc", new { name = Path.GetFileName(path), source = source }));
            OnPropertyChanged(nameof(SelectedPlcName));
        }
    });

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
