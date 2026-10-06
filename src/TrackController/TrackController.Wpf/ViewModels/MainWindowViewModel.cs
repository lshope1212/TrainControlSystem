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

/// <summary>Binds operator controls and observed outputs to the live and isolated test hosts.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly TrackControllerService _live;
    private readonly TrackControllerService _test;
    private WaysidePipeHost _host;
    private readonly WaysideEndpoints _liveEndpoints;
    private string? _liveDeploymentPath, _testDeploymentPath;
    private string _liveDeploymentDescription = "Local PC preview · all blocks", _testDeploymentDescription = "Local PC preview · all blocks";
    private WaysidePipeHost _testHost;
    private readonly WaysideTestConnection _testConnection;
    private readonly WaysidePipeClient _liveSender;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private static readonly JsonSerializerOptions ConfigurationJson = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };
    private static readonly WaysideSnapshotMessage NoTestOutput = new("HW-TC01", "Green Line", DateTimeOffset.MinValue, DateTimeOffset.MinValue, 0, true, 1, true, false, "Waiting for controller output", "No output received.", [], []);
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;
    private DateTimeOffset _lastHeartbeat;
    private bool _isTest;
    private string _status = "Live feedback selected. Open Controller setup to choose PLC or software logic.";
    private string _connection = "Starting local connection…";
    private string _selectedBlockId = "5";
    private WaysideSnapshotMessage _snapshot = null!;
    private IReadOnlyList<MovementRow> _movements = [];
    private IReadOnlyList<EquipmentRow> _equipment = [];
    private string[] _blockIds = [], _switchIds = [], _signalIds = [], _crossingIds = [];
    private bool _disposed;
    private bool _keepCurrent = true;
    private WaysideFeedbackMessage? _appliedFeedback;
    private WaysideMovementRequestMessage? _appliedRequest;
    private string _plcSource = "Select a .plc file to view its Boolean program.";
    private bool _hardwareMode = true;
    private bool _maintenanceMode;
    private int _speedMultiplier = 1;
    private int _tabIndex;
    /// <summary>Receives separate live and test controllers and wires their UI commands and transports.</summary>
    public MainWindowViewModel(TrackControllerService liveController, TrackControllerService testController, WaysideEndpoints? liveEndpoints = null)
    {
        ArgumentNullException.ThrowIfNull(liveController);
        ArgumentNullException.ThrowIfNull(testController);
        if (ReferenceEquals(liveController, testController))
        {
            throw new ArgumentException("Live and test controllers must have separate state.");
        }

        _live = liveController;
        _test = testController;
        _dispatcher = Dispatcher.CurrentDispatcher;
        var endpoints = liveEndpoints ?? WaysideEndpoints.Live;
        _liveEndpoints = endpoints;
        _liveSender = new WaysidePipeClient(endpoints.Input);
        _host = new WaysidePipeHost(_live, endpoints);
        _testConnection = new WaysideTestConnection(WaysideEndpoints.CreateIsolated());
        _testHost = new WaysidePipeHost(_test, _testConnection.Endpoints);
        _testConnection.StatusReceived += text => Post(() =>
        {
            if (IsTest)
                Status = text;
        });
        _testConnection.AcknowledgementReceived += receipt => Post(() =>
        {
            LastReceipt = $"{(receipt.IsAccepted ? "Accepted" : "Rejected")}: {receipt.Detail}";
            OnPropertyChanged(nameof(LastReceipt));
            if (IsTest && !receipt.IsAccepted)
                Status = $"Controller rejected input: {receipt.Detail}";
        });
        ObserveLiveHost();
        UseTestUiCommand = Command(() => SelectModeAsync(true));
        UseLiveFeedbackCommand = Command(() => SelectModeAsync(false));
        PauseResumeCommand = Command(() => SendAsync(new WaysideSimulationMessage { IsPaused = !Snapshot.IsPaused, SpeedMultiplier = SpeedMultiplier }));
        LoadPlcCommand = Command(ChoosePlcAsync);
        LoadSamplePlcCommand = Command(() => LoadPlcFileAsync(Path.Combine(AppContext.BaseDirectory, "Samples", "wayside.plc")));
        LoadLayoutCommand = Command(ChooseLayoutAsync);
        LoadDeploymentCommand = Command(ChooseDeploymentAsync);
        LoadSampleDeploymentCommand = Command(() => ReplaceDeploymentAsync(Path.Combine(AppContext.BaseDirectory, "Samples", "all-pc.deployment.json")));
        ApplyExecutionCommand = Command(ApplyExecutionAsync);
        SaveExecutionCommand = Command(SaveExecutionAsync);
        RestoreLocalDemoCommand = Command(RestoreLocalDemoAsync);
        SendMovementCommand = TestCommand(SendMovementAsync);
        CancelMovementCommand = TestCommand(async () =>
        {
            await SendAsync(new WaysideMovementRequestMessage { RequestId = _appliedRequest?.RequestId ?? Guid.NewGuid().ToString("N"), TargetBlockId = TargetBlock, IsActive = false });
            _appliedRequest = null;
        });
        SendSwitchCommand = TestCommand(() => SendAsync(new SwitchPositionRequestMessage { BlockId = SwitchTarget, RequestedPosition = RequestedSwitch }));
        SendMaintenanceCommand = TestCommand(() => SendAsync(new MaintenanceRequestMessage { BlockId = MaintenanceTarget, RequestedState = RequestedMaintenance }));
        ApplyFeedbackCommand = TestCommand(ApplyFeedbackAsync);
        LoadScenarioCommand = TestCommand(() =>
        {
            LoadScenario();
            return Task.CompletedTask;
        });
        SendManualCommand = new AsyncRelayCommand(() => RunAsync(SendManualAsync), () => !_disposed);
        ResetTestCommand = TestCommand(ResetTestAsync);
        Refresh();
        CreateFeedbackRows();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick(), _dispatcher);
        _testConnection.Start();
        _testHost.Start();
        _host.Start();
    }

    public ICommand UseTestUiCommand
    {
        get;
    }
    public ICommand UseLiveFeedbackCommand
    {
        get;
    }
    public ICommand PauseResumeCommand
    {
        get;
    }
    public ICommand LoadPlcCommand
    {
        get;
    }
    public ICommand LoadSamplePlcCommand
    {
        get;
    }
    public ICommand LoadLayoutCommand
    {
        get;
    }
    public ICommand LoadDeploymentCommand
    {
        get;
    }
    public ICommand LoadSampleDeploymentCommand
    {
        get;
    }
    public ICommand ApplyExecutionCommand
    {
        get;
    }
    public ICommand SaveExecutionCommand
    {
        get;
    }
    public ICommand RestoreLocalDemoCommand
    {
        get;
    }
    public ICommand SendMovementCommand
    {
        get;
    }
    public ICommand CancelMovementCommand
    {
        get;
    }
    public ICommand SendSwitchCommand
    {
        get;
    }
    public ICommand SendMaintenanceCommand
    {
        get;
    }
    public ICommand ApplyFeedbackCommand
    {
        get;
    }
    public ICommand LoadScenarioCommand
    {
        get;
    }
    public ICommand SendManualCommand
    {
        get;
    }
    public ICommand ResetTestCommand
    {
        get;
    }

    private AsyncRelayCommand Command(Func<Task> action) => new(() => RunAsync(action), () => !_disposed);
    private AsyncRelayCommand TestCommand(Func<Task> action) => new(() => RunAsync(action), () => IsTest && !_disposed);
    public bool IsTest => _isTest;
    public bool IsLive => !IsTest;
    public string ModeDescription => IsTest ? "Test UI · Views show the test deployment. Live module traffic stays separate." : "Live feedback · All views show data from CTC and Track Model.";
    public string[] ControllerModes { get; } = ["PLC program", "Software logic"];
    public string ControllerMode
    {
        get => IsHardwareMode ? ControllerModes[0] : ControllerModes[1]; set => IsHardwareMode = value == ControllerModes[0];
    }
    public bool IsRoutedDeployment => (IsTest ? _testDeploymentPath : _liveDeploymentPath) is not null;
    public bool CanChooseLogic => !IsRoutedDeployment;
    public string DeploymentDescription => IsTest ? _testDeploymentDescription : _liveDeploymentDescription;
    public string LastReceipt { get; private set; } = "No controller receipt yet.";
    public string Status
    {
        get => _status; private set => SetProperty(ref _status, value);
    }
    public string Connection
    {
        get => IsTest ? $"Test pipe transport · sent {_testConnection.SentCount} · CTC outputs {_testConnection.CtcReceivedCount} · Track Model outputs {_testConnection.TrackModelReceivedCount}" : _connection; private set => SetProperty(ref _connection, value);
    }
    public string PlcSource
    {
        get => _plcSource; private set => SetProperty(ref _plcSource, value);
    }
    public string PlcSummary => Snapshot.IsHardwareMode ? Snapshot.PlcStatus.Replace("hardware mode", "PLC logic") : "Software logic active. A PLC program is not required.";
    public WaysideSnapshotMessage Snapshot => _snapshot;
    public IReadOnlyList<WaysideBlockStatus> Blocks => _snapshot.Blocks;
    public IReadOnlyList<MovementRow> Movements => _movements;
    public IReadOnlyList<EquipmentRow> Equipment => _equipment;
    public ObservableCollection<FeedbackRow> FeedbackRows { get; } = [];
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
                NotifySelection();
        }
    }

    public WaysideBlockStatus? SelectedBlock => Blocks.FirstOrDefault(blockStatus => blockStatus.BlockId == SelectedBlockId);
    public MovementRow? SelectedMovement => Movements.FirstOrDefault(movement => movement.Target == SelectedBlockId);
    public string SelectedLimit => SelectedBlock is { } blockStatus ? $"Track speed limit: {DisplayUnits.FormatMph(blockStatus.SpeedLimitMetersPerSecond)} mph" : "";
    public string SelectedExecutionTarget => (IsTest ? _testHost : _host).GetOwnerDescription(SelectedBlockId);
    public string ActualSpeed => SelectedBlock?.ActualSpeedMetersPerSecond is double speed ? $"{DisplayUnits.FormatMph(speed)} mph" : "Unavailable";
    public string SimulationStatus => _snapshot.IsPaused ? "Paused" : $"Running · {_snapshot.SpeedMultiplier:0}×";
    public string SimulationTime => TimeSpan.FromSeconds(_snapshot.SimulationSeconds).ToString(@"hh\:mm\:ss");
    public string PauseResumeLabel => _snapshot.IsPaused ? "Resume" : "Pause";
    public string DataStatus => (IsTest && !_testConnection.IsSnapshotCurrent ? "Controller output unavailable · " : "") + $"Track feedback: {Blocks.Count(blockStatus => blockStatus.IsFresh)}/{Blocks.Count} current  ·  Logic: {(_snapshot.IsHardwareMode ? "PLC program" : "Software")}  ·  {_snapshot.Authorizations.Count} movement request(s)";
    public string LastResult => IsTest && !_testConnection.IsSnapshotCurrent ? "No current controller output. Check the pipe connection." : _movements.LastOrDefault() is { } movement ? $"{movement.Status} — {movement.AuthorizedSpeed} mph / {movement.AuthorizedAuthority} ft\n{movement.Reason}" : "No movement request submitted.";
    public int TabIndex
    {
        get => _tabIndex; set => SetProperty(ref _tabIndex, value);
    }

    public bool ShouldRepeatInputs
    {
        get => _keepCurrent;
        set
        {
            if (SetProperty(ref _keepCurrent, value))
                OnPropertyChanged(nameof(RepeatDescription));
        }
    }

    public string RepeatDescription => ShouldRepeatInputs ? "Repeats the last sent feedback and movement request. Unsent edits are not included." : "Automatic repeats are off. Sent feedback and requests will expire.";

    public bool IsHardwareMode
    {
        get => _hardwareMode;
        set
        {
            if (SetProperty(ref _hardwareMode, value))
                _ = RunAsync(() => SendAsync(new WaysideControllerModeMessage { IsHardwareMode = value }));
        }
    }

    public bool IsMaintenanceMode
    {
        get => _maintenanceMode;
        set
        {
            if (SetProperty(ref _maintenanceMode, value))
                _ = RunAsync(() => SendAsync(new WaysideMaintenanceModeMessage { IsEnabled = value }));
        }
    }

    public int SpeedMultiplier
    {
        get => _speedMultiplier;
        set
        {
            if (SetProperty(ref _speedMultiplier, value))
                _ = RunAsync(() => SendAsync(new WaysideSimulationMessage { IsPaused = _snapshot.IsPaused, SpeedMultiplier = value }));
        }
    }

    public int[] SpeedMultipliers { get; } = Enumerable.Range(1, 10).ToArray();

    private async Task SelectModeAsync(bool test)
    {
        if (IsTest == test)
        {
            if (test)
                TabIndex = 3;
            return;
        }

        await SendAsync(new WaysideSimulationMessage { IsPaused = true, SpeedMultiplier = Snapshot.SpeedMultiplier });
        if (test)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            deadline.CancelAfter(3000);
            // Wait for actual published output before binding the test forms to its territory.
            while (!_testConnection.IsSnapshotCurrent)
                await Task.Delay(15, deadline.Token);
        }

        _isTest = test;
        SyncMode();
        Refresh();
        TabIndex = test ? 3 : 0;
        foreach (string property in new[]
        {
            nameof(IsTest),
            nameof(IsLive),
            nameof(ModeDescription),
            nameof(IsRoutedDeployment),
            nameof(CanChooseLogic),
            nameof(DeploymentDescription)
        }

        )
            OnPropertyChanged(property);
        NotifyExecutionEditor();
        CommandManager.InvalidateRequerySuggested();
        Status = test ? "Test UI ready. Send track feedback, resume, then send a movement request. Choose controller logic in Test setup." : "Live feedback selected. Resume when ready to evaluate live requests.";
    }

    private void SyncMode()
    {
        var snapshot = IsTest ? _testConnection.LatestSnapshot ?? NoTestOutput : _host.LatestSnapshot;
        _hardwareMode = snapshot.IsHardwareMode;
        _maintenanceMode = snapshot.IsMaintenanceMode;
        _speedMultiplier = (int)snapshot.SpeedMultiplier;
        PlcSource = IsTest ? _testPlcSource : _livePlcSource;
        OnPropertyChanged(nameof(IsHardwareMode));
        OnPropertyChanged(nameof(ControllerMode));
        OnPropertyChanged(nameof(IsMaintenanceMode));
        OnPropertyChanged(nameof(SpeedMultiplier));
    }

    private string _testPlcSource = "Select a .plc file to view its Boolean program.";
    private string _livePlcSource = "Select a .plc file to view its Boolean program.";
    private async void Tick()
    {
        if (_disposed || !await _operations.WaitAsync(0))
            return;
        try
        {
            if ((DateTimeOffset.UtcNow - _lastHeartbeat).TotalSeconds >= 1)
            {
                _lastHeartbeat = DateTimeOffset.UtcNow;
                if (IsTest && ShouldRepeatInputs)
                {
                    if (_appliedFeedback is not null)
                        await ApplyLatchedFeedbackAsync();
                    if (_appliedRequest is not null)
                        await SendAsync(_appliedRequest, announce: false);
                }
            }

            if (!_disposed)
                Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            if (!_disposed)
            {
                Status = $"Test input send failed: {ex.Message}";
                Refresh();
            }
        }
        finally
        {
            _operations.Release();
        }
    }

    private void Refresh()
    {
        _snapshot = IsTest ? _testConnection.LatestSnapshot ?? NoTestOutput : _host.LatestSnapshot;
        _hardwareMode = _snapshot.IsHardwareMode;
        _maintenanceMode = _snapshot.IsMaintenanceMode;
        _speedMultiplier = (int)_snapshot.SpeedMultiplier;
        OnPropertyChanged(nameof(IsHardwareMode));
        OnPropertyChanged(nameof(ControllerMode));
        OnPropertyChanged(nameof(IsMaintenanceMode));
        OnPropertyChanged(nameof(SpeedMultiplier));
        bool current = !IsTest || _testConnection.IsSnapshotCurrent;
        if (!current)
            _snapshot = _snapshot with
            {
                Blocks = _snapshot.Blocks.Select(blockStatus => blockStatus with { IsFresh = false, Status = "Controller output unavailable" }).ToArray()
            };
        _movements = _snapshot.Authorizations.Select(authorization => new MovementRow(authorization, current)).ToArray();
        _equipment = Blocks.SelectMany(blockStatus =>
        {
            var rows = new List<EquipmentRow>();
            string status = blockStatus.Status;
            if (blockStatus.HasSwitch)
                rows.Add(new($"Switch {blockStatus.BlockId}", blockStatus.BlockId, blockStatus.CommandedSwitch.ToString(), blockStatus.ReportedSwitch.ToString(), status));
            if (blockStatus.HasSignal)
                rows.Add(new($"Signal {blockStatus.BlockId}", blockStatus.BlockId, blockStatus.CommandedSignal.ToString(), blockStatus.ReportedSignal.ToString(), status));
            if (blockStatus.HasCrossing)
                rows.Add(new($"Crossing {blockStatus.BlockId}", blockStatus.BlockId, blockStatus.CommandedCrossing.ToString(), blockStatus.ReportedCrossing.ToString(), status));
            return current ? rows : rows.Select(equipment => equipment with { Command = "—", Feedback = "—", Status = "Controller output unavailable" }).ToList();
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
            OnPropertyChanged(propertyName);
        RefreshBlockChoices();
        RefreshDraftSelections();
        if (IsTest && Blocks.Count > 0 && !FeedbackRows.Select(feedbackRow => feedbackRow.BlockId).SequenceEqual(BlockIds))
            CreateFeedbackRows();
        NotifySelection();
        OnPropertyChanged(nameof(CanEditExecution));
        OnPropertyChanged(nameof(CanApplyExecution));
    }

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
            OnPropertyChanged(propertyName);
    }

    private void RefreshBlockChoices()
    {
        // Choices describe configuration, not feedback freshness. Keep them while awaiting a new
        // deployment's first output, and avoid resetting WPF selectors for every network snapshot.
        if (Blocks.Count == 0)
            return;
        Update(ref _blockIds, Blocks.Select(blockStatus => blockStatus.BlockId).ToArray(), nameof(BlockIds));
        Update(ref _switchIds, Blocks.Where(blockStatus => blockStatus.HasSwitch).Select(blockStatus => blockStatus.BlockId).ToArray(), nameof(SwitchIds));
        Update(ref _signalIds, Blocks.Where(blockStatus => blockStatus.HasSignal).Select(blockStatus => blockStatus.BlockId).ToArray(), nameof(SignalIds));
        Update(ref _crossingIds, Blocks.Where(blockStatus => blockStatus.HasCrossing).Select(blockStatus => blockStatus.BlockId).ToArray(), nameof(CrossingIds));
        void Update(ref string[] current, string[] next, string property)
        {
            if (current.SequenceEqual(next))
                return;
            current = next;
            OnPropertyChanged(property);
        }
    }

    private void Post(Action action)
    {
        if (!_disposed)
            _dispatcher.BeginInvoke(() =>
            {
                if (!_disposed)
                    action();
            });
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_disposed)
            return;
        await _operations.WaitAsync();
        try
        {
            if (!_disposed)
                await action();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or TimeoutException or OperationCanceledException)
        {
            if (!_disposed)
                Status = ex.Message;
        }
        finally
        {
            if (!_disposed)
                Refresh();
            _operations.Release();
        }
    }

    private async Task SendAsync(object message, bool announce = true)
    {
        if (IsTest)
            await _testConnection.SendAsync(message, _lifetime.Token);
        else
            await _liveSender.SendAsync(message, _lifetime.Token);
        if (announce)
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

    private async Task ChoosePlcAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load Boolean PLC program",
            Filter = "PLC programs (*.plc;*.txt)|*.plc;*.txt|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
            await LoadPlcFileAsync(dialog.FileName);
    }

    private async Task LoadPlcFileAsync(string path)
    {
        await SendAsync(new WaysideSimulationMessage { IsPaused = true, SpeedMultiplier = Snapshot.SpeedMultiplier });
        if (new FileInfo(path).Length > 65536)
            throw new ArgumentException("PLC file exceeds 64 KiB.");
        string source = File.ReadAllText(path);
        PlcSource = source;
        if (IsTest)
            _testPlcSource = source;
        else
            _livePlcSource = source;
        if (IsTest)
            _testProgramName = Path.GetFileName(path);
        else
            _liveProgramName = Path.GetFileName(path);
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
            return;
        if (new FileInfo(dialog.FileName).Length > 1048576)
            throw new ArgumentException("Territory file exceeds 1 MiB.");
        var config = JsonSerializer.Deserialize<WaysideConfigurationMessage>(File.ReadAllText(dialog.FileName), ConfigurationJson) ?? throw new ArgumentException("Empty configuration.");
        await SendAsync(config);
        _appliedFeedback = null;
        _appliedRequest = null;
        SelectedBlockId = config.Blocks.FirstOrDefault()?.BlockId ?? "";
        Status = "Territory configuration sent. Check the controller response before resuming.";
    }

    private static double ParseNumber(string text, string field)
    {
        if (!double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out double result) || !double.IsFinite(result) || result < 0)
            throw new ArgumentException($"{field} must be a finite, nonnegative number.");
        return result;
    }

    /// <summary>Stops UI updates and awaits both hosts and test listeners before shutdown.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        await _lifetime.CancelAsync();
        await _operations.WaitAsync();
        try
        {
            await _host.DisposeAsync();
            await _testHost.DisposeAsync();
            await _testConnection.DisposeAsync();
        }
        finally
        {
            _operations.Release();
        }
    }
}
