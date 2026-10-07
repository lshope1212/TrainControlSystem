using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using TrainControl.Contracts.Enums;
using TrainController.Integration;
using TrainController.Integration.Presentation;
using TrainController.Integration.Simulation;
using TrainController.Integration.State;
using TrainController.Wpf.Commands;

namespace TrainController.Wpf.ViewModels;

/// <summary>
/// Test UI: replaces the Train Model during Train Controller testing. Exposes ONLY
/// Train-Model -> Train-Controller inputs (per-train <see cref="TestModelState"/>) and
/// Train-Controller -> Train-Model outputs, plus the GLOBAL Test Mode switch and test
/// simulation controls. Driver and Engineer inputs deliberately stay in the Main UI.
/// </summary>
/// <remarks>
/// The selected train here is independent of the Main UI's selection and never affects
/// which trains run: a train runs while its Test Model state says it is in service.
/// Numeric fields are imperial in the UI and stored in SI.
/// </remarks>
public sealed class TestWindowViewModel : ViewModelBase
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    private readonly TrainControllerSubsystem _subsystem;
    private DispatcherTimer? _timer;
    private TrainChoice _selectedTrain;
    private TestUiOutputs _outputs = new TestUiOutputs();
    private string _actualSpeedMphText = string.Empty;
    private string _authorizedSpeedMphText = string.Empty;
    private string _remainingAuthorityFeetText = string.Empty;
    private string _distanceToStationFeetText = string.Empty;
    private string _cabinTemperatureFahrenheitText = string.Empty;
    private string _inputError = string.Empty;
    private string _simulationText = string.Empty;
    private string _headerTimeText = DisplayUnits.SimulationTime(0);
    private string _beaconPendingText = string.Empty;
    private string _authorityPendingText = string.Empty;
    private string _commandMessage = string.Empty;

    public TestWindowViewModel(TrainControllerSubsystem subsystem)
    {
        _subsystem = subsystem ?? throw new ArgumentNullException(nameof(subsystem));
        Trains = TrainChoice.AllTrains;
        _selectedTrain = Trains[0];

        Fleet = new ObservableCollection<TestFleetRowStatus>(_subsystem.Registry.Trains.Select(TrainControllerPresenter.BuildTestFleetRow));
        InputWarnings = new ObservableCollection<string>();

        RunCommand = new RelayCommand(_ => Run(), _ => IsTestMode && !IsRunning);
        StopCommand = new AsyncRelayCommand(_ => StopAsync(), _ => IsRunning);
        StepCommand = new AsyncRelayCommand(_ => StepAsync(), _ => IsTestMode && !IsRunning);
        ResetCommand = new AsyncRelayCommand(_ => ResetAsync());
        TransmitBeaconCommand = new RelayCommand(_ => Model.TransmitBeacon());
        SendAuthorityCommand = new RelayCommand(_ => Model.SendAuthorityUpdate());

        _subsystem.Simulation.StateChanged += (_, _) => OnSimulationStateChanged();

        LoadEditableState();
        Refresh();
    }

    // ------------------------------------------------------------------ selection

    public IReadOnlyList<TrainChoice> Trains { get; }

    public TrainChoice SelectedTrain
    {
        get => _selectedTrain;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedTrain))
            {
                return;
            }

            _selectedTrain = value;
            OnPropertyChanged();
            LoadEditableState();
            Refresh();
        }
    }

    public string SelectedTrainId => _selectedTrain.TrainId;

    // ------------------------------------------------------------------ global Test Mode + simulation

    /// <summary>GLOBAL: true = every train takes model input from the Test UI; false = from the Train Model.</summary>
    public bool IsTestMode
    {
        get => _subsystem.TestMode.IsTestMode;
        set
        {
            _subsystem.TestMode.SetTestMode(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNormalMode));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsNormalMode
    {
        get => !IsTestMode;
        set => IsTestMode = !value;
    }

    public bool IsRunning => _subsystem.Simulation.State == TestSimulationState.Running;

    public IReadOnlyList<int> SpeedMultipliers => _subsystem.Simulation.SupportedSpeedMultipliers;

    public int SelectedSpeedMultiplier
    {
        get => _subsystem.Simulation.SpeedMultiplier;
        set
        {
            _subsystem.Simulation.SetSpeedMultiplier(value);
            OnPropertyChanged();
        }
    }

    public string SimulationText
    {
        get => _simulationText;
        private set => SetProperty(ref _simulationText, value);
    }

    public string HeaderTimeText
    {
        get => _headerTimeText;
        private set => SetProperty(ref _headerTimeText, value);
    }

    /// <summary>Last command outcome (e.g. "Run requires Test Mode").</summary>
    public string CommandMessage
    {
        get => _commandMessage;
        private set => SetProperty(ref _commandMessage, value);
    }

    public ICommand RunCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand StepCommand { get; }

    /// <summary>Test SIMULATION reset for all trains (not the Driver's E-brake reset).</summary>
    public ICommand ResetCommand { get; }

    // ------------------------------------------------------------------ Train Model -> Train Controller (selected train)

    /// <summary>Simulated "dispatched / in service" state from the Train Model side.</summary>
    public bool IsActive
    {
        get => Model.IsActive;
        set
        {
            Model.IsActive = value;
            OnPropertyChanged();
        }
    }

    public string ActualSpeedMphText
    {
        get => _actualSpeedMphText;
        set
        {
            if (SetProperty(ref _actualSpeedMphText, value))
            {
                ApplyNumber(value, "Actual speed", mph => Model.ActualSpeedMetersPerSecond = DisplayUnits.FromMph(mph));
            }
        }
    }

    public string AuthorizedSpeedMphText
    {
        get => _authorizedSpeedMphText;
        set
        {
            if (SetProperty(ref _authorizedSpeedMphText, value))
            {
                ApplyNumber(value, "Authorized speed", mph => Model.AuthorizedSpeedMetersPerSecond = DisplayUnits.FromMph(mph));
            }
        }
    }

    public string RemainingAuthorityFeetText
    {
        get => _remainingAuthorityFeetText;
        set
        {
            if (SetProperty(ref _remainingAuthorityFeetText, value))
            {
                ApplyNumber(value, "Remaining authority", feet => Model.RemainingAuthorityMeters = DisplayUnits.FromFeet(feet));
            }
        }
    }

    /// <summary>Simulates the Train Model delivering the current authority value as a NEW update.</summary>
    public ICommand SendAuthorityCommand { get; }

    public string AuthorityPendingText
    {
        get => _authorityPendingText;
        private set => SetProperty(ref _authorityPendingText, value);
    }

    public bool TrackSignalValid
    {
        get => Model.TrackSignalValid;
        set
        {
            Model.TrackSignalValid = value;
            OnPropertyChanged();
        }
    }

    public bool BeaconValid
    {
        get => Model.BeaconValid;
        set
        {
            Model.BeaconValid = value;
            OnPropertyChanged();
        }
    }

    public string NextStationName
    {
        get => Model.NextStationName;
        set
        {
            Model.NextStationName = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    public string DistanceToStationFeetText
    {
        get => _distanceToStationFeetText;
        set
        {
            if (SetProperty(ref _distanceToStationFeetText, value))
            {
                ApplyNumber(value, "Distance to next station", feet => Model.DistanceToNextStationMeters = DisplayUnits.FromFeet(feet));
            }
        }
    }

    public IReadOnlyList<PlatformSide> PlatformSides { get; } = Enum.GetValues<PlatformSide>();

    public PlatformSide PlatformSide
    {
        get => Model.PlatformSide;
        set
        {
            Model.PlatformSide = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Simulates the train passing a NEW beacon with the beacon fields above.</summary>
    public ICommand TransmitBeaconCommand { get; }

    public string BeaconPendingText
    {
        get => _beaconPendingText;
        private set => SetProperty(ref _beaconPendingText, value);
    }

    public bool PassengerEmergencyBrakeRequested
    {
        get => Model.PassengerEmergencyBrakeRequested;
        set
        {
            Model.PassengerEmergencyBrakeRequested = value;
            OnPropertyChanged();
        }
    }

    public bool LeftDoorsOpen
    {
        get => Model.LeftDoorsOpen;
        set
        {
            Model.LeftDoorsOpen = value;
            OnPropertyChanged();
        }
    }

    public bool RightDoorsOpen
    {
        get => Model.RightDoorsOpen;
        set
        {
            Model.RightDoorsOpen = value;
            OnPropertyChanged();
        }
    }

    public bool ExteriorLightsOn
    {
        get => Model.ExteriorLightsOn;
        set
        {
            Model.ExteriorLightsOn = value;
            OnPropertyChanged();
        }
    }

    public string CabinTemperatureFahrenheitText
    {
        get => _cabinTemperatureFahrenheitText;
        set
        {
            if (SetProperty(ref _cabinTemperatureFahrenheitText, value))
            {
                ApplyNumber(value, "Cabin temperature", f => Model.CabinTemperatureCelsius = DisplayUnits.FromFahrenheit(f));
            }
        }
    }

    public string InputError
    {
        get => _inputError;
        private set => SetProperty(ref _inputError, value);
    }

    /// <summary>Edge-case / fault-injection warnings for the selected train's inputs (allowed, but visible).</summary>
    public ObservableCollection<string> InputWarnings { get; }

    // ------------------------------------------------------------------ Train Controller -> Train Model (selected train)

    public TestUiOutputs Outputs
    {
        get => _outputs;
        private set => SetProperty(ref _outputs, value);
    }

    public ObservableCollection<TestFleetRowStatus> Fleet { get; }

    // ------------------------------------------------------------------ lifecycle

    public void StartLiveUpdates()
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    public void StopLiveUpdates()
    {
        _timer?.Stop();
        _timer = null;
    }

    public void Refresh()
    {
        Outputs = TrainControllerPresenter.BuildTestOutputs(Slot);

        for (var i = 0; i < _subsystem.Registry.Trains.Count; i++)
        {
            var row = TrainControllerPresenter.BuildTestFleetRow(_subsystem.Registry.Trains[i]);
            if (!Equals(Fleet[i], row))
            {
                Fleet[i] = row;
            }
        }

        var warnings = TrainControllerPresenter.TestInputWarnings(Model.PeekModelInput(), _subsystem.Options.Vehicle);
        if (!InputWarnings.SequenceEqual(warnings))
        {
            InputWarnings.Clear();
            foreach (var warning in warnings)
            {
                InputWarnings.Add(warning);
            }
        }

        BeaconPendingText = Model.BeaconReceptionPending ? "Beacon queued for this train's next tick." : string.Empty;
        AuthorityPendingText = Model.AuthorityUpdatePending ? "Update queued for next tick." : string.Empty;
        HeaderTimeText = DisplayUnits.SimulationTime(_subsystem.Engine.SimulationTimeSeconds);
        SimulationText =
            $"{(IsRunning ? "Running" : "Stopped")}  ·  Tick {_subsystem.Engine.TickCount}" +
            $"  ·  dt {DisplayUnits.Number(_subsystem.Simulation.DeltaTimeSeconds, 2)} s" +
            $"  ·  {SelectedSpeedMultiplier}x";
    }

    // ------------------------------------------------------------------ simulation actions (public for tests)

    public bool Run()
    {
        var started = _subsystem.Simulation.Run();
        CommandMessage = started ? string.Empty : "Run requires Test Mode and a stopped simulation.";
        OnSimulationStateChanged();
        return started;
    }

    public async Task StopAsync()
    {
        await _subsystem.Simulation.StopAsync();
        OnSimulationStateChanged();
    }

    public async Task StepAsync()
    {
        var report = await _subsystem.Simulation.StepAsync();
        CommandMessage = report is null
            ? "Step requires Test Mode and a stopped simulation."
            : $"Stepped tick {report.TickId} ({report.ExecutedTrainIds.Count} active train(s)).";
        Refresh();
    }

    public async Task ResetAsync()
    {
        await _subsystem.Simulation.ResetAsync();
        CommandMessage = "Test simulation reset for all trains (inputs and Kp/Ki kept).";
        Refresh();
    }

    // ------------------------------------------------------------------ helpers

    private TrainRuntimeSlot Slot => _subsystem.Registry.Get(_selectedTrain.TrainId);

    private TestModelState Model => Slot.TestModel;

    private void OnSimulationStateChanged()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsTestMode));
        OnPropertyChanged(nameof(IsNormalMode));
        CommandManager.InvalidateRequerySuggested();
    }

    private void ApplyNumber(string text, string field, Action<double> apply)
    {
        if (DisplayUnits.TryParseNumber(text, out var value))
        {
            apply(value);
            InputError = string.Empty;
        }
        else
        {
            InputError = $"{field}: enter a number.";
        }
    }

    private void LoadEditableState()
    {
        var model = Model;
        _actualSpeedMphText = DisplayUnits.Number(DisplayUnits.ToMph(model.ActualSpeedMetersPerSecond));
        _authorizedSpeedMphText = DisplayUnits.Number(DisplayUnits.ToMph(model.AuthorizedSpeedMetersPerSecond));
        _remainingAuthorityFeetText = DisplayUnits.Number(DisplayUnits.ToFeet(model.RemainingAuthorityMeters), 0);
        _distanceToStationFeetText = DisplayUnits.Number(DisplayUnits.ToFeet(model.DistanceToNextStationMeters), 0);
        _cabinTemperatureFahrenheitText = DisplayUnits.Number(DisplayUnits.ToFahrenheit(model.CabinTemperatureCelsius));
        InputError = string.Empty;

        foreach (var name in new[]
        {
            nameof(SelectedTrainId), nameof(IsActive), nameof(ActualSpeedMphText), nameof(AuthorizedSpeedMphText),
            nameof(RemainingAuthorityFeetText), nameof(TrackSignalValid), nameof(BeaconValid), nameof(NextStationName),
            nameof(DistanceToStationFeetText), nameof(PlatformSide), nameof(PassengerEmergencyBrakeRequested),
            nameof(LeftDoorsOpen), nameof(RightDoorsOpen), nameof(ExteriorLightsOn), nameof(CabinTemperatureFahrenheitText),
        })
        {
            OnPropertyChanged(name);
        }
    }
}
