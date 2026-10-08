using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using TrackController.Contracts.Messages;
using TrackController.TestUI.Wpf.Commands;
using TrackController.TestUI.Wpf.Services;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.TestUI.Wpf.ViewModels;

/// <summary>Sends simulated external-module inputs and displays only received controller outputs.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly WaysideTestConnection _testConnection;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private static readonly WaysideSnapshotMessage NoTestOutput = new("HW-TC01", "No line loaded", DateTimeOffset.MinValue, DateTimeOffset.MinValue, 0, true, 1, true, false, "Waiting for controller output", "No output received.", [], []);
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;
    private DateTimeOffset _lastHeartbeat;
    private string _status = "Start TrackController.Wpf and configure its layout. This window simulates CTC and Track Model inputs.";
    private string _selectedBlockId = "5";
    private WaysideSnapshotMessage _snapshot = null!;
    private IReadOnlyList<MovementRow> _movements = [];
    private IReadOnlyList<EquipmentRow> _equipment = [];
    private string[] _blockIds = [], _switchIds = [], _signalIds = [], _crossingIds = [];
    private bool _disposed;
    private bool _keepCurrent = true;
    private WaysideFeedbackMessage? _appliedFeedback;
    private WaysideMovementRequestMessage? _appliedRequest;

    /// <summary>Connects simulated peers to the same pipes used by the real modules.</summary>
    public MainWindowViewModel(string? inputPipe = null, string? ctcOutput = null, string? trackModelOutput = null)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _testConnection = new WaysideTestConnection(inputPipe ?? NamedPipeNames.TrackController, ctcOutput ?? NamedPipeNames.Ctc, trackModelOutput ?? "TrainControl.TrackModel");
        _testConnection.StatusReceived += text => Post(() => Status = text);
        _testConnection.AcknowledgementReceived += receipt => Post(() =>
        {
            LastReceipt = $"{(receipt.IsAccepted ? "Accepted" : "Rejected")}: {receipt.Detail}";
            OnPropertyChanged(nameof(LastReceipt));
            if (receipt.MessageType == nameof(SwitchPositionRequestMessage))
            {
                SwitchRequestResult = LastReceipt;
                OnPropertyChanged(nameof(SwitchRequestResult));
            }
            else if (receipt.MessageType == nameof(MaintenanceRequestMessage))
            {
                ClosureRequestResult = LastReceipt;
                OnPropertyChanged(nameof(ClosureRequestResult));
            }

            if (!receipt.IsAccepted)
            {
                Status = $"Controller rejected input: {receipt.Detail}";
            }
        });
        SendMovementCommand = Command(SendMovementAsync);
        CancelMovementCommand = new AsyncRelayCommand(() => RunAsync(CancelMovementAsync), () => !_disposed && _appliedRequest is not null);
        SendSwitchCommand = Command(() => SendAsync(new SwitchPositionRequestMessage { BlockId = SwitchTarget, RequestedPosition = RequestedSwitch }));
        SendMaintenanceCommand = Command(() => SendAsync(new MaintenanceRequestMessage { BlockId = MaintenanceTarget, RequestedState = RequestedMaintenance }));
        ApplyFeedbackCommand = Command(ApplyFeedbackAsync);
        LoadScenarioCommand = Command(() =>
        {
            LoadScenario();
            return Task.CompletedTask;
        });
        ClearInputsCommand = Command(ClearInputsAsync);
        Refresh();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick(), _dispatcher);
        _testConnection.Start();
    }

    public ICommand SendMovementCommand { get; }

    public ICommand CancelMovementCommand { get; }

    public ICommand SendSwitchCommand { get; }

    public ICommand SendMaintenanceCommand { get; }

    public ICommand ApplyFeedbackCommand { get; }

    public ICommand LoadScenarioCommand { get; }

    private AsyncRelayCommand Command(Func<Task> action) => new(() => RunAsync(action), () => !_disposed);

    public string LastReceipt { get; private set; } = "No controller receipt yet.";

    public string SwitchRequestResult { get; private set; } = "No switch request sent.";

    public string ClosureRequestResult { get; private set; } = "No closure request sent.";

    public string CancelMovementLabel => _appliedRequest is { } request ? $"Cancel block {request.TargetBlockId} movement" : "Cancel last movement";

    public string MovementRequestSummary => _appliedRequest is { } request ? $"Cancels only the last movement sent from this window, for block {request.TargetBlockId}, even if the draft target changes. Other requests are unaffected." : "No last sent movement to cancel. Each Send creates a movement request for the target block.";

    public string SwitchFeedbackStatus
    {
        get
        {
            if (!_testConnection.IsSnapshotCurrent)
            {
                return "Waiting for current controller output.";
            }

            var block = Blocks.FirstOrDefault(block => block.BlockId == SwitchTarget && block.HasSwitch);
            if (block is null)
            {
                return "Select a switch.";
            }

            string reported = block.IsFresh ? block.ReportedSwitch.ToString() : "unavailable";
            string waiting = block.IsFresh && block.CommandedSwitch == block.ReportedSwitch ? "" : " Waiting for matching Track Model feedback.";
            return $"Switch {block.BlockId}: commanded {block.CommandedSwitch}, reported {reported}.{waiting}";
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public WaysideSnapshotMessage Snapshot => _snapshot;

    public IReadOnlyList<WaysideBlockStatus> Blocks => _snapshot.Blocks;

    public IReadOnlyList<MovementRow> Movements => _movements;

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
            {

            }
        }
    }

    public bool ShouldRepeatInputs
    {
        get => _keepCurrent;
        set
        {
            if (SetProperty(ref _keepCurrent, value))
            {
                OnPropertyChanged(nameof(RepeatDescription));
            }
        }
    }

    public string RepeatDescription => ShouldRepeatInputs ? "Refreshes the last sent feedback and movement every second to prevent timeouts. New edits still require Send." : "Refreshes stopped to test timeouts. Feedback and movement requests expire unless sent again.";

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

    private static double ParseNumber(string text, string field)
    {
        if (!double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out double result) || !double.IsFinite(result) || result < 0)
        {
            throw new ArgumentException($"{field} must be a finite, nonnegative number.");
        }

        return result;
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

    public IReadOnlyList<EquipmentRow> Equipment => _equipment;

    public ICommand ClearInputsCommand { get; }

    public string DataStatus => _testConnection.IsSnapshotCurrent ? $"Connected · {Blocks.Count(block => block.IsFresh)}/{Blocks.Count} feedback blocks current" : "Waiting for TrackController.Wpf output. Start the controller and check its connections.";

    public string LastResult
    {
        get
        {
            if (!_testConnection.IsSnapshotCurrent)
            {
                return "No current controller output. Check the pipe connection.";
            }

            var movement = _appliedRequest is { } request ? Movements.SingleOrDefault(row => row.Value.RequestId == request.RequestId) : Movements.LastOrDefault();
            return movement is not null ? $"Block {movement.Target}: {movement.Status} — {movement.AuthorizedSpeed} mph / route limit {movement.AuthorizedAuthority} ft\n{movement.Reason}" : _appliedRequest is not null ? "Waiting for the controller response to the updated movement request." : "No movement requests in controller output.";
        }
    }

    private void Refresh()
    {
        _snapshot = _testConnection.LatestSnapshot ?? NoTestOutput;
        bool current = _testConnection.IsSnapshotCurrent;
        if (!current)
        {
            _snapshot = _snapshot with { Blocks = _snapshot.Blocks.Select(block => block with { IsFresh = false, Status = "Controller output unavailable" }).ToArray() };
        }
        _movements = _snapshot.Authorizations.Select(authorization => new MovementRow(authorization, current)).ToArray();
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
                rows.Add(new($"Signal {blockStatus.BlockId}", blockStatus.BlockId, blockStatus.CommandedSignal.ToString().Replace("SuperGreen", "Super Green"), blockStatus.ReportedSignal.ToString().Replace("SuperGreen", "Super Green"), status));
            }

            if (blockStatus.HasCrossing)
            {
                rows.Add(new($"Crossing {blockStatus.BlockId}", blockStatus.BlockId, blockStatus.CommandedCrossing.ToString(), blockStatus.ReportedCrossing.ToString(), status));
            }

            return current ? rows : rows.Select(equipment => equipment with { Command = "—", Feedback = "—", Status = "Controller output unavailable" }).ToList();
        }).ToArray();
        RefreshBlockChoices();
        RefreshDraftSelections();
        if (Blocks.Count > 0 && !FeedbackRows.Select(row => row.BlockId).SequenceEqual(BlockIds))
        {
            _appliedFeedback = null;
            SetAppliedRequest(null);
            ApplyLayoutDefaults();
            CreateFeedbackRows();
            OnPropertyChanged(nameof(ScenarioDescription));
        }
        foreach (var name in new[] { nameof(Snapshot), nameof(Blocks), nameof(Equipment), nameof(Movements), nameof(DataStatus), nameof(LastResult), nameof(SwitchFeedbackStatus), nameof(RequestedAuthority) })
        {
            OnPropertyChanged(name);
        }
    }

    private async Task CancelMovementAsync()
    {
        if (_appliedRequest is not { } request)
        {
            return;
        }

        await SendAsync(new WaysideMovementRequestMessage { RequestId = request.RequestId, TargetBlockId = request.TargetBlockId, IsActive = false });
        SetAppliedRequest(null);
        MovementProgress = "Movement cancelled. Occupancy changes will not send another request until you select Send movement request.";
        Status = $"Cancellation sent for the last movement from block {request.TargetBlockId}. Its repeats have stopped. Other requests are unaffected.";
    }

    private void SetAppliedRequest(WaysideMovementRequestMessage? request)
    {
        _appliedRequest = request;
        _movementOriginObserved = request is not null && _appliedFeedback?.Blocks.Any(block => block.BlockId == request.TargetBlockId && block.Occupancy == OccupancyState.Occupied) == true;
        _nextBlockObservedClear = request is not null && _appliedFeedback?.Blocks.Any(block => block.BlockId == request.PathBlockIds.FirstOrDefault() && block.Occupancy == OccupancyState.Clear) == true;
        if (request is null)
        {
            MovementProgress = "Send a movement request to start following its route.";
        }
        OnPropertyChanged(nameof(CancelMovementLabel));
        OnPropertyChanged(nameof(MovementRequestSummary));
        OnPropertyChanged(nameof(LastResult));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task ClearInputsAsync()
    {
        ShouldRepeatInputs = false;
        _appliedFeedback = null;
        if (_appliedRequest is not null)
        {
            await CancelMovementAsync();
        }
        _testConnection.ClearObservations();
        CreateFeedbackRows();
        LastReceipt = "Test inputs cleared. Waiting for controller output.";
        OnPropertyChanged(nameof(LastReceipt));
        Status = "Repeats stopped and test drafts cleared. Last sent feedback expires normally; reset the controller in its own window when needed.";
    }

    private async void Tick()
    {
        if (_disposed || !await _operations.WaitAsync(0))
        {
            return;
        }

        try
        {
            if ((DateTimeOffset.UtcNow - _lastHeartbeat).TotalSeconds >= 1)
            {
                _lastHeartbeat = DateTimeOffset.UtcNow;
                if (ShouldRepeatInputs)
                {
                    if (_appliedFeedback is not null)
                    {
                        await ApplyLatchedFeedbackAsync();
                    }

                    if (_appliedRequest is not null && !HasMovementOriginCleared)
                    {
                        await SendAsync(_appliedRequest, announce: false);
                    }
                }
            }

            if (!_disposed)
            {
                Refresh();
            }
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

    private async Task SendAsync(object message, bool announce = true)
    {
        await _testConnection.SendAsync(message, _lifetime.Token);

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

    /// <summary>Stops UI updates and awaits the simulated peer listeners before shutdown.</summary>
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
            await _testConnection.DisposeAsync();
        }
        finally
        {
            _operations.Release();
        }
    }

}
