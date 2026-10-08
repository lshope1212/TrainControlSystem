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
    private static readonly WaysideSnapshotMessage NoTestOutput = new("HW-TC01", "Green Line", DateTimeOffset.MinValue, DateTimeOffset.MinValue, 0, true, 1, true, false, "Waiting for controller output", "No output received.", [], []);
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
            if (!receipt.IsAccepted)
            {
                Status = $"Controller rejected input: {receipt.Detail}";
            }
        });
        SendMovementCommand = Command(SendMovementAsync);
        CancelMovementCommand = Command(CancelMovementAsync);
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

    public string RepeatDescription => ShouldRepeatInputs ? "Repeats the last sent feedback and movement request. Unsent edits are not included." : "Automatic repeats are off. Sent feedback and requests will expire.";

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

    public string Connection => $"Module pipe transport · sent {_testConnection.SentCount} · CTC outputs {_testConnection.CtcReceivedCount} · Track Model outputs {_testConnection.TrackModelReceivedCount}";

    public string DataStatus => _testConnection.IsSnapshotCurrent ? $"Connected · {Blocks.Count(block => block.IsFresh)}/{Blocks.Count} feedback blocks current" : "Waiting for TrackController.Wpf output. Start the controller and check its connections.";

    public string LastResult => !_testConnection.IsSnapshotCurrent ? "No current controller output. Check the pipe connection." : Movements.LastOrDefault() is { } movement ? $"{movement.Status} — {movement.AuthorizedSpeed} mph / {movement.AuthorizedAuthority} ft\n{movement.Reason}" : "No movement request submitted.";

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
                rows.Add(new($"Signal {blockStatus.BlockId}", blockStatus.BlockId, blockStatus.CommandedSignal.ToString(), blockStatus.ReportedSignal.ToString(), status));
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
            _appliedRequest = null;
            CreateFeedbackRows();
        }
        foreach (var name in new[] { nameof(Snapshot), nameof(Blocks), nameof(Equipment), nameof(Movements), nameof(Connection), nameof(DataStatus), nameof(LastResult) })
        {
            OnPropertyChanged(name);
        }
    }

    private async Task CancelMovementAsync()
    {
        await SendAsync(new WaysideMovementRequestMessage { RequestId = _appliedRequest?.RequestId ?? Guid.NewGuid().ToString("N"), TargetBlockId = TargetBlock, IsActive = false });
        _appliedRequest = null;
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

                    if (_appliedRequest is not null)
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
