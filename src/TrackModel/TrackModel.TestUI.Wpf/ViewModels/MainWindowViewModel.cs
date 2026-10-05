using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using TrackModel.TestUI.Wpf.Commands;
using TrackModel.TestUI.Wpf.Services;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackModel.TestUI.Wpf.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly ExternalModuleSimulator _connection;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<string, CapturedBlockViewModel> _captured = [];
    private readonly Dictionary<string, int> _ticketRates = [];
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _inputDebounce;
    private readonly HashSet<InputGroup> _pendingInputs = [];
    private bool _suppressInputs, _sendingInputs, _stopped;
    private Guid _layoutSnapshotId;
    private bool _layoutInputsPending, _trainInputsPending;
    private enum InputGroup { Controller, Train, Failures, Time }
    private bool _clockBusy;
    private string _blockId = "104", _currentBlock = "104", _trainId = "01";
    private CapturedBlockViewModel? _selectedOutput;
    private string _status = "Disconnected";
    private string _speed = "25", _authority = "1200", _actualSpeed = "22", _boarding = "0", _disembarking = "0";
    private string _time = "09:42:18", _multiplier = "1";
    private SwitchPosition _switch = SwitchPosition.Normal;
    private SignalState _signal = SignalState.Green;
    private CrossingState _crossing = CrossingState.Open;
    private OccupancyState _trainOccupancy = OccupancyState.Occupied;
    private bool _brokenRail, _circuit, _power;
    private bool _isClockRunning;
    public MainWindowViewModel(ExternalModuleSimulator connection)
    {
        _connection = connection;
        _connection.MessageReceived += Receive;
        _connection.ErrorReported += error => Status = "Capture error: " + error;
        SendCommandsCommand = new AsyncRelayCommand(SendCommandsAsync);
        SendTrainCommand = new AsyncRelayCommand(() => SendTrainAsync(exchange: true));
        SendFailuresCommand = new AsyncRelayCommand(SendFailuresAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        SendTimeCommand = new AsyncRelayCommand(SendTimeAsync);
        StepClockCommand = new AsyncRelayCommand(() => AdvanceClockAsync(10));
        ToggleClockCommand = new RelayCommand(ToggleClock);
        ClearLogCommand = new RelayCommand(() => Messages.Clear());
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += async (_, _) => await AdvanceClockAsync(1);
        _inputDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _inputDebounce.Tick += async (_, _) => await FlushInputsAsync();
    }

    public ObservableCollection<TrackBlockDefinition> Blocks { get; } = [];
    public ObservableCollection<CapturedBlockViewModel> OutputBlocks { get; } = [];
    public ObservableCollection<ReceivedMessage> Messages { get; } = [];
    public SwitchPosition[] SwitchOptions { get; } = [SwitchPosition.Normal, SwitchPosition.Reverse];
    public SignalState[] SignalOptions { get; } = [SignalState.Green, SignalState.Yellow, SignalState.Red];
    public CrossingState[] CrossingOptions { get; } = [CrossingState.Open, CrossingState.Closed];
    public OccupancyState[] TrainOccupancyOptions { get; } = [OccupancyState.Occupied, OccupancyState.Clear];
    public ICommand SendCommandsCommand { get; }
    public ICommand SendTrainCommand { get; }
    public ICommand SendFailuresCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SendTimeCommand { get; }
    public ICommand StepClockCommand { get; }
    public ICommand ToggleClockCommand { get; }
    public ICommand ClearLogCommand { get; }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public string BlockId
    {
        get => _blockId;
        set
        {
            // WPF temporarily clears selections when the layout collection is rebuilt.
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!SetProperty(ref _blockId, value)) return;
            _pendingInputs.Remove(InputGroup.Controller);
            _pendingInputs.Remove(InputGroup.Failures);
            SelectOutput(value);
            LoadCapturedInputs();
            OnPropertyChanged(nameof(SelectedCommandBlock));
            OnPropertyChanged(nameof(HasSwitch)); OnPropertyChanged(nameof(HasSignal)); OnPropertyChanged(nameof(HasCrossing));
        }
    }
    public string CurrentBlock
    {
        get => _currentBlock;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || !SetProperty(ref _currentBlock, value)) return;
            SelectOutput(value);
            OnPropertyChanged(nameof(SelectedTrainBlock));
            QueueInput(InputGroup.Train);
        }
    }
    public string TrainId { get => _trainId; set { if (SetProperty(ref _trainId, value)) QueueInput(InputGroup.Train); } }
    public OccupancyState TrainOccupancy
    {
        get => _trainOccupancy;
        set
        {
            if (value is not (OccupancyState.Occupied or OccupancyState.Clear)) return;
            if (SetProperty(ref _trainOccupancy, value)) QueueInput(InputGroup.Train);
        }
    }
    public TrackBlockDefinition? SelectedCommandBlock
    {
        get => Blocks.FirstOrDefault(b => b.BlockId == BlockId);
        set { if (value is not null) BlockId = value.BlockId; }
    }
    public TrackBlockDefinition? SelectedTrainBlock
    {
        get => Blocks.FirstOrDefault(b => b.BlockId == CurrentBlock);
        set { if (value is not null) CurrentBlock = value.BlockId; }
    }
    public string Speed { get => _speed; set { if (SetProperty(ref _speed, value)) QueueInput(InputGroup.Controller); } }
    public string Authority { get => _authority; set { if (SetProperty(ref _authority, value)) QueueInput(InputGroup.Controller); } }
    public string ActualSpeed { get => _actualSpeed; set { if (SetProperty(ref _actualSpeed, value)) QueueInput(InputGroup.Train); } }
    public string Boarding { get => _boarding; set => SetProperty(ref _boarding, value); }
    public string Disembarking { get => _disembarking; set => SetProperty(ref _disembarking, value); }
    public string Time { get => _time; set { if (SetProperty(ref _time, value)) QueueInput(InputGroup.Time); } }
    public string Multiplier { get => _multiplier; set => SetProperty(ref _multiplier, value); }
    public SwitchPosition Switch { get => _switch; set { if (SetProperty(ref _switch, value)) QueueInput(InputGroup.Controller); } }
    public SignalState Signal { get => _signal; set { if (SetProperty(ref _signal, value)) QueueInput(InputGroup.Controller); } }
    public CrossingState Crossing { get => _crossing; set { if (SetProperty(ref _crossing, value)) QueueInput(InputGroup.Controller); } }
    public bool BrokenRail { get => _brokenRail; set { if (SetProperty(ref _brokenRail, value)) QueueInput(InputGroup.Failures); } }
    public bool Circuit { get => _circuit; set { if (SetProperty(ref _circuit, value)) QueueInput(InputGroup.Failures); } }
    public bool Power { get => _power; set { if (SetProperty(ref _power, value)) QueueInput(InputGroup.Failures); } }
    public bool HasSwitch => Blocks.FirstOrDefault(b => b.BlockId == BlockId)?.HasSwitch == true;
    public bool HasSignal => Blocks.FirstOrDefault(b => b.BlockId == BlockId)?.HasSignal == true;
    public bool HasCrossing => Blocks.FirstOrDefault(b => b.BlockId == BlockId)?.HasCrossing == true;
    public string ClockAction => _isClockRunning ? "Pause clock" : "Start clock";
    public CapturedBlockViewModel? SelectedOutput
    {
        get => _selectedOutput;
        set { if (SetProperty(ref _selectedOutput, value)) OnPropertyChanged(nameof(TicketSales)); }
    }
    public string TicketSales
    {
        get
        {
            var lineId = _lineByBlock.GetValueOrDefault(SelectedOutput?.Id ?? "");
            return lineId is not null && _ticketRates.TryGetValue(lineId, out var rate) ? $"{rate:N0} tickets/hour ({lineId})" : "—";
        }
    }
    private readonly Dictionary<string, string> _lineByBlock = [];

    private async Task SendCommandsAsync() => await SendSafely(() => new TrackModelCommandMessage
    {
        BlockId = Required(BlockId, "Block"), CommandedSpeedMetersPerSecond = Number(Speed, "Speed") * 0.44704,
        AuthorityMeters = Number(Authority, "Authority") * 0.3048, Switch = Switch, Signal = Signal, Crossing = Crossing
    });

    private async Task SendTrainAsync(bool exchange = false)
    {
        var remove = TrainOccupancy == OccupancyState.Clear;
        _pendingInputs.Remove(InputGroup.Train);
        SelectOutput(CurrentBlock);
        await SendSafely(() => new TrackModelTrainUpdateMessage
        {
            TrainId = Required(TrainId, "Train ID"), CurrentBlockId = remove ? "" : Required(CurrentBlock, "Current block"),
            ActualSpeedMetersPerSecond = remove ? 0 : Number(ActualSpeed, "Actual speed") * 0.44704,
            BoardingPassengers = !remove && exchange ? Count(Boarding, "Boarding") : 0,
            DisembarkingPassengers = !remove && exchange ? Count(Disembarking, "Disembarking") : 0,
            ExchangeId = Guid.NewGuid().ToString("N")
        });
    }

    private async Task SendFailuresAsync() => await SendSafely(() => new TrackModelFailureCommandMessage
        { BlockId = Required(BlockId, "Block"), BrokenRail = BrokenRail, TrackCircuitFailure = Circuit, PowerFailure = Power });

    public Task RefreshAsync() => SendSafely(() => new TrackModelSnapshotRequestMessage());
    private Task SendTimeAsync() => SendSafely(() => new SystemTimeMessage { SystemTime = ParseTime() });

    private async Task SendSafely(Func<object> build)
    {
        try
        {
            var message = build();
            Status = "Sending input to Track Model…";
            await _connection.SendAsync(message);
        }
        catch (TimeoutException) { Status = "Track Model offline"; }
        catch (Exception ex) { Status = "Unable to send: " + ex.Message; }
    }

    private void Receive(string destination, MessageEnvelope envelope)
    {
        var wasSuppressed = _suppressInputs;
        _suppressInputs = true;
        try
        {
            switch (envelope.MessageType)
            {
                case nameof(TrackLayoutMessage):
                    var layout = MessageSerializer.DeserializePayload<TrackLayoutMessage>(envelope);
                    var selection = BlockId;
                    var current = CurrentBlock;
                    _layoutSnapshotId = layout.SnapshotId;
                    _layoutInputsPending = true;
                    _trainInputsPending = true;
                    _pendingInputs.Clear(); _inputDebounce.Stop();
                    Blocks.Clear(); _lineByBlock.Clear();
                    var ids = new HashSet<string>();
                    foreach (var line in layout.Lines)
                        foreach (var block in line.Blocks)
                        {
                            Blocks.Add(block); ids.Add(block.BlockId); _lineByBlock[block.BlockId] = line.LineId;
                            GetCaptured(block.BlockId);
                        }
                    foreach (var stale in _captured.Keys.Where(id => !ids.Contains(id)).ToList())
                    { OutputBlocks.Remove(_captured[stale]); _captured.Remove(stale); }
                    BlockId = ids.Contains(selection) ? selection : Blocks.FirstOrDefault()?.BlockId ?? "";
                    CurrentBlock = ids.Contains(current) ? current : Blocks.FirstOrDefault()?.BlockId ?? "";
                    // WPF finishes its collection-change selection work after this callback.
                    // Restore the source selections then, rather than during that update.
                    _dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                    {
                        OnPropertyChanged(nameof(SelectedCommandBlock)); OnPropertyChanged(nameof(SelectedTrainBlock));
                    }));
                    SelectOutput(BlockId);
                    OnPropertyChanged(nameof(HasSwitch)); OnPropertyChanged(nameof(HasSignal)); OnPropertyChanged(nameof(HasCrossing)); OnPropertyChanged(nameof(TicketSales));
                    TrySynchronizeLayoutInputs();
                    break;
                case nameof(TrackModelBlockStateMessage):
                    var state = MessageSerializer.DeserializePayload<TrackModelBlockStateMessage>(envelope);
                    var initialState = GetCaptured(state.BlockId).State is null;
                    GetCaptured(state.BlockId).Apply(state);
                    TrySynchronizeLayoutInputs();
                    if (_layoutSnapshotId == Guid.Empty && initialState && state.BlockId == BlockId && _pendingInputs.Count == 0) LoadCapturedInputs();
                    break;
                case nameof(TrackModelTrainEnvironmentMessage):
                    var environment = MessageSerializer.DeserializePayload<TrackModelTrainEnvironmentMessage>(envelope);
                    var initialEnvironment = GetCaptured(environment.BlockId).Environment is null;
                    GetCaptured(environment.BlockId).Apply(environment);
                    TrySynchronizeLayoutInputs();
                    if (_layoutSnapshotId == Guid.Empty && initialEnvironment && environment.BlockId == BlockId && _pendingInputs.Count == 0) LoadCapturedInputs();
                    break;
                case nameof(TrackModelSignalMessage):
                    var signal = MessageSerializer.DeserializePayload<TrackModelSignalMessage>(envelope);
                    GetCaptured(signal.BlockId).Apply(signal); break;
                case nameof(TicketSalesMessage):
                    var sales = MessageSerializer.DeserializePayload<TicketSalesMessage>(envelope);
                    _ticketRates[sales.LineId] = sales.TicketsPerHour; OnPropertyChanged(nameof(TicketSales)); break;
                case nameof(TrackModelInputResultMessage):
                    var result = MessageSerializer.DeserializePayload<TrackModelInputResultMessage>(envelope);
                    Status = result.Accepted ? "Input accepted" : "Rejected: " + result.Detail;
                    break;
                default: break; // Keep unfamiliar contract messages visible in the log.
            }
            Messages.Insert(0, new(DateTime.Now.ToString("HH:mm:ss"), destination, envelope.MessageType, envelope.Payload.ToString()));
            while (Messages.Count > 200) Messages.RemoveAt(Messages.Count - 1);
        }
        catch (Exception ex) { Status = "Output could not be decoded: " + ex.Message; }
        finally { _suppressInputs = wasSuppressed; }
    }

    private CapturedBlockViewModel GetCaptured(string id)
    {
        if (_captured.TryGetValue(id, out var existing)) return existing;
        var block = new CapturedBlockViewModel(id);
        _captured.Add(id, block); OutputBlocks.Add(block);
        if (id == BlockId && SelectedOutput is null) SelectedOutput = block;
        return block;
    }
    private void SelectOutput(string? id)
    {
        if (id is not null && _captured.TryGetValue(id, out var block)) SelectedOutput = block;
    }
    private void LoadCapturedInputs()
    {
        var wasSuppressed = _suppressInputs;
        _suppressInputs = true;
        try
        {
            var state = _captured.GetValueOrDefault(BlockId)?.State;
            if (state is not null)
            {
                BrokenRail = state.BrokenRail; Circuit = state.TrackCircuitFailure; Power = state.PowerFailure;
                Switch = state.Switch == SwitchPosition.Reverse ? SwitchPosition.Reverse : SwitchPosition.Normal;
                Signal = state.Signal is SignalState.Green or SignalState.Yellow ? state.Signal : SignalState.Red;
                Crossing = state.Crossing == CrossingState.Closed ? CrossingState.Closed : CrossingState.Open;
            }
            var environment = _captured.GetValueOrDefault(BlockId)?.Environment;
            if (environment is not null)
            {
                Speed = (environment.CommandedSpeedMetersPerSecond / 0.44704).ToString("0.###", CultureInfo.CurrentCulture);
                Authority = (environment.AuthorityMeters / 0.3048).ToString("0.###", CultureInfo.CurrentCulture);
            }
        }
        finally { _suppressInputs = wasSuppressed; }
    }

    private void TrySynchronizeLayoutInputs()
    {
        if (_layoutInputsPending)
        {
            var captured = _captured.GetValueOrDefault(BlockId);
            if (_layoutSnapshotId == Guid.Empty ||
                (captured?.State?.SnapshotId == _layoutSnapshotId && captured.Environment?.SnapshotId == _layoutSnapshotId))
            {
                LoadCapturedInputs();
                _layoutInputsPending = false;
            }
        }
        if (_trainInputsPending)
        {
            var environment = _captured.GetValueOrDefault(CurrentBlock)?.Environment;
            if (environment is null || environment.SnapshotId != _layoutSnapshotId) return;
            // Physical occupancy comes from the train, even when a circuit failure
            // makes the wayside's reported occupancy Unknown.
            TrainOccupancy = environment.TrainId.Length > 0 ? OccupancyState.Occupied : OccupancyState.Clear;
            if (environment.TrainId.Length > 0) TrainId = environment.TrainId;
            ActualSpeed = (environment.ActualSpeedMetersPerSecond / 0.44704).ToString("0.###", CultureInfo.CurrentCulture);
            _trainInputsPending = false;
        }
    }

    private void QueueInput(InputGroup group)
    {
        if (_suppressInputs || _stopped) return;
        // An edit made while the fresh snapshot is arriving belongs to the user.
        if (group is InputGroup.Controller or InputGroup.Failures) _layoutInputsPending = false;
        if (group == InputGroup.Train) _trainInputsPending = false;
        if (group is InputGroup.Controller or InputGroup.Failures) SelectOutput(BlockId);
        _pendingInputs.Add(group);
        _inputDebounce.Stop();
        _inputDebounce.Start();
    }

    private async Task FlushInputsAsync()
    {
        _inputDebounce.Stop();
        if (_sendingInputs || _stopped) return;
        _sendingInputs = true;
        var groups = _pendingInputs.ToArray();
        _pendingInputs.Clear();
        try
        {
            foreach (var group in groups)
                switch (group)
                {
                    case InputGroup.Controller: await SendCommandsAsync(); break;
                    case InputGroup.Train: await SendTrainAsync(); break;
                    case InputGroup.Failures: await SendFailuresAsync(); break;
                    case InputGroup.Time: await SendTimeAsync(); break;
                }
        }
        finally
        {
            _sendingInputs = false;
            if (_pendingInputs.Count > 0 && !_stopped) _inputDebounce.Start();
        }
    }

    private void ToggleClock()
    {
        _isClockRunning = !_isClockRunning;
        if (_isClockRunning) _clock.Start(); else _clock.Stop();
        OnPropertyChanged(nameof(ClockAction));
    }
    private async Task AdvanceClockAsync(double seconds)
    {
        if (_clockBusy) return;
        _clockBusy = true;
        try
        {
            var time = ParseTime() + TimeSpan.FromSeconds(seconds * Number(Multiplier, "Clock multiplier"));
            await _connection.SendAsync(new SystemTimeMessage { SystemTime = time });
            // The clock already sent this value; do not enqueue a second manual edit.
            _time = time.ToString(@"hh\:mm\:ss");
            OnPropertyChanged(nameof(Time));
        }
        catch (Exception ex)
        {
            _clock.Stop(); _isClockRunning = false; OnPropertyChanged(nameof(ClockAction));
            Status = "Clock stopped: " + ex.Message;
        }
        finally { _clockBusy = false; }
    }
    public void Stop() { _stopped = true; _clock.Stop(); _inputDebounce.Stop(); _pendingInputs.Clear(); }
    private TimeSpan ParseTime()
    {
        if (!TimeSpan.TryParseExact(Time, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException("System time must use HH:mm:ss.");
        return value;
    }
    private static double Number(string text, string name)
    {
        if (!double.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) || !double.IsFinite(value) || value < 0)
            throw new ArgumentException(name + " must be a nonnegative number.");
        return value;
    }
    private static int Count(string text, string name)
    {
        if (!int.TryParse(text, out var value) || value < 0) throw new ArgumentException(name + " must be a nonnegative whole number.");
        return value;
    }
    private static string Required(string text, string name) => string.IsNullOrWhiteSpace(text)
        ? throw new ArgumentException(name + " is required.") : text.Trim();
}
