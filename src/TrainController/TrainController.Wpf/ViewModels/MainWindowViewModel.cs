using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using TrainController.Abstractions.Fleet;
using TrainController.Abstractions.Inputs;
using TrainController.Integration;
using TrainController.Integration.Hardware;
using TrainController.Integration.Logging;
using TrainController.Integration.Presentation;
using TrainController.Integration.State;
using TrainController.Wpf.Commands;

namespace TrainController.Wpf.ViewModels;

/// <summary>
/// Main UI (Driver / Engineer). Edits go to the SELECTED train's Driver state and Engineer
/// settings; selecting a train changes only what is shown/edited — it never starts, stops or
/// resets any train. All control decisions are made by the controllers; this class only binds.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    /// <summary>UI refresh period (display only; unrelated to the controller timestep).</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    private readonly TrainControllerSubsystem _subsystem;
    private DispatcherTimer? _timer;
    private TrainChoice _selectedTrain;
    private MainUiTrainStatus _status;
    private string _kpText = string.Empty;
    private string _kiText = string.Empty;
    private string _gainsMessage = string.Empty;
    private string _cabinSetpointText = string.Empty;
    private string _requestedSpeedText = string.Empty;
    private string _announcementText = string.Empty;
    private DisplayValue _requestedSpeedMessage = new DisplayValue(string.Empty);
    private string _inputMessage = string.Empty;
    private string _headerTimeText = DisplayUnits.SimulationTime(0);
    private string _inputSourceText = string.Empty;
    private string _statusBarText = string.Empty;
    private string _pendingPressText = string.Empty;
    private TrainControllerLogEntry? _lastLogEntry;

    public MainWindowViewModel(TrainControllerSubsystem subsystem)
    {
        _subsystem = subsystem ?? throw new ArgumentNullException(nameof(subsystem));

        Trains = TrainChoice.AllTrains;
        _selectedTrain = Trains[0];
        _status = TrainControllerPresenter.BuildMain(Slot, HardwareState);

        Fleet = new ObservableCollection<FleetRowStatus>(_subsystem.Registry.Trains.Select(TrainControllerPresenter.BuildFleetRow));
        Alerts = new ObservableCollection<string>();
        EventLog = new ObservableCollection<TrainControllerLogEntry>();

        EmergencyBrakeCommand = new RelayCommand(_ => Slot.Driver.PressEmergencyBrake());
        EmergencyBrakeResetCommand = new RelayCommand(_ => Slot.Driver.PressEmergencyBrakeReset());
        ApplyGainsCommand = new RelayCommand(_ => ApplyGains());
        ApplyCabinSetpointCommand = new RelayCommand(_ => ApplyCabinSetpoint());
        ApplyRequestedSpeedCommand = new RelayCommand(_ => ApplyRequestedSpeed(), _ => IsManual);
        AnnounceCommand = new RelayCommand(_ => Announce(), _ => !string.IsNullOrWhiteSpace(AnnouncementText));

        LoadEditableState();
        Refresh();
    }

    // ------------------------------------------------------------------ selection

    /// <summary>TRAIN-001 … TRAIN-010 with their fixed controller type.</summary>
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

    // ------------------------------------------------------------------ read-only display

    /// <summary>Snapshot of the selected train, replaced on every refresh.</summary>
    public MainUiTrainStatus Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public ObservableCollection<FleetRowStatus> Fleet { get; }

    public ObservableCollection<string> Alerts { get; }

    public ObservableCollection<TrainControllerLogEntry> EventLog { get; }

    public string HeaderTimeText
    {
        get => _headerTimeText;
        private set => SetProperty(ref _headerTimeText, value);
    }

    /// <summary>"INPUT: TRAIN MODEL" or "TEST MODE — INPUT: TEST UI".</summary>
    public string InputSourceText
    {
        get => _inputSourceText;
        private set => SetProperty(ref _inputSourceText, value);
    }

    public string StatusBarText
    {
        get => _statusBarText;
        private set => SetProperty(ref _statusBarText, value);
    }

    public string PendingPressText
    {
        get => _pendingPressText;
        private set => SetProperty(ref _pendingPressText, value);
    }

    /// <summary>"Nominal max 43.5 mph" hint next to the requested-speed input.</summary>
    public string NominalMaxSpeedText => $"Nominal max {DisplayUnits.Speed(_subsystem.Options.Vehicle.MaxSpeedMetersPerSecond)}";

    // ------------------------------------------------------------------ Driver inputs (selected train)

    public bool IsManual
    {
        get => Slot.Driver.Mode == OperatingMode.Manual;
        set
        {
            if (value)
            {
                SetMode(OperatingMode.Manual);
            }
        }
    }

    public bool IsAutomatic
    {
        get => Slot.Driver.Mode == OperatingMode.Automatic;
        set
        {
            if (value)
            {
                SetMode(OperatingMode.Automatic);
            }
        }
    }

    /// <summary>
    /// Requested speed entry text in mph (Manual mode only). Editing it changes nothing until
    /// <see cref="ApplyRequestedSpeedCommand"/> (Enter / Set). Reloaded only when the selected
    /// train changes, so periodic refresh never overwrites typing.
    /// </summary>
    public string RequestedSpeedText
    {
        get => _requestedSpeedText;
        set => SetProperty(ref _requestedSpeedText, value);
    }

    /// <summary>Validation / confirmation for the requested-speed entry.</summary>
    public DisplayValue RequestedSpeedMessage
    {
        get => _requestedSpeedMessage;
        private set => SetProperty(ref _requestedSpeedMessage, value);
    }

    /// <summary>Validates the entry (mph) and stores it in the selected train's Driver state (m/s).</summary>
    public ICommand ApplyRequestedSpeedCommand { get; }

    public bool ServiceBrakeRequested
    {
        get => Slot.Driver.ServiceBrakeRequested;
        set
        {
            Slot.Driver.ServiceBrakeRequested = value;
            OnPropertyChanged();
        }
    }

    public bool LeftDoorsOpenRequested
    {
        get => Slot.Driver.LeftDoorsOpenRequested;
        set
        {
            Slot.Driver.LeftDoorsOpenRequested = value;
            OnPropertyChanged();
        }
    }

    public bool RightDoorsOpenRequested
    {
        get => Slot.Driver.RightDoorsOpenRequested;
        set
        {
            Slot.Driver.RightDoorsOpenRequested = value;
            OnPropertyChanged();
        }
    }

    public bool ExteriorLightsRequested
    {
        get => Slot.Driver.ExteriorLightsRequested;
        set
        {
            Slot.Driver.ExteriorLightsRequested = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Cabin temperature request in °F (UI unit); applied with <see cref="ApplyCabinSetpointCommand"/>.</summary>
    public string CabinSetpointText
    {
        get => _cabinSetpointText;
        set => SetProperty(ref _cabinSetpointText, value);
    }

    public string InputMessage
    {
        get => _inputMessage;
        private set => SetProperty(ref _inputMessage, value);
    }

    /// <summary>Driver announcement text (Manual and Automatic). Sent once with <see cref="AnnounceCommand"/>.</summary>
    public string AnnouncementText
    {
        get => _announcementText;
        set => SetProperty(ref _announcementText, value);
    }

    /// <summary>Queues the announcement for the SELECTED train's next tick (one-shot), then clears the box.</summary>
    public ICommand AnnounceCommand { get; }

    public ICommand EmergencyBrakeCommand { get; }

    /// <summary>Driver E-brake reset (NOT the Test UI simulation Reset).</summary>
    public ICommand EmergencyBrakeResetCommand { get; }

    public ICommand ApplyCabinSetpointCommand { get; }

    // ------------------------------------------------------------------ Engineer (selected train)

    /// <summary>Kp in SI terms: W per (m/s) of speed error.</summary>
    public string KpText
    {
        get => _kpText;
        set => SetProperty(ref _kpText, value);
    }

    /// <summary>Ki in SI terms: W per (m/s·s) of integrated speed error.</summary>
    public string KiText
    {
        get => _kiText;
        set => SetProperty(ref _kiText, value);
    }

    public string GainsMessage
    {
        get => _gainsMessage;
        private set => SetProperty(ref _gainsMessage, value);
    }

    public ICommand ApplyGainsCommand { get; }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Starts periodic display refresh on the calling (UI) thread. Called by the window.</summary>
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

    /// <summary>Re-reads display state for the selected train and the fleet. Never changes controller state.</summary>
    public void Refresh()
    {
        Status = TrainControllerPresenter.BuildMain(Slot, HardwareState);

        for (var i = 0; i < _subsystem.Registry.Trains.Count; i++)
        {
            var row = TrainControllerPresenter.BuildFleetRow(_subsystem.Registry.Trains[i]);
            if (!Equals(Fleet[i], row))
            {
                Fleet[i] = row;
            }
        }

        if (!Alerts.SequenceEqual(Status.Alerts))
        {
            Alerts.Clear();
            foreach (var alert in Status.Alerts)
            {
                Alerts.Add(alert);
            }
        }

        RefreshEventLog();

        var testMode = _subsystem.TestMode.IsTestMode;
        InputSourceText = testMode ? "TEST MODE  ·  INPUT: TEST UI" : "INPUT: TRAIN MODEL";
        HeaderTimeText = DisplayUnits.SimulationTime(_subsystem.Engine.SimulationTimeSeconds);

        var driver = Slot.Driver;
        PendingPressText = driver.EmergencyBrakePressPending
            ? "E-brake press pending (applied on this train's next tick)"
            : driver.EmergencyBrakeResetPending
                ? "E-brake reset pending (applied on this train's next tick)"
                : string.Empty;

        var running = _subsystem.Registry.Trains.Count(s => s.LastModelInput?.IsActive == true);
        StatusBarText =
            $"{TrainFleet.TrainCount} trains  ·  {TrainFleet.SoftwareTrainIds.Count} Software  ·  {TrainFleet.HardwareTrainIds.Count} Hardware" +
            $"  ·  {running} running  ·  Tick {_subsystem.Engine.TickCount}" +
            $"  ·  Pi link: {TrainControllerPresenter.HardwareLinkText(HardwareState).Text}";
    }

    // ------------------------------------------------------------------ helpers

    private TrainRuntimeSlot Slot => _subsystem.Registry.Get(_selectedTrain.TrainId);

    private HardwareConnectionState? HardwareState => _subsystem.HardwareConnection?.ConnectionState;

    private void SetMode(OperatingMode mode)
    {
        Slot.Driver.Mode = mode;
        OnPropertyChanged(nameof(IsManual));
        OnPropertyChanged(nameof(IsAutomatic));
        CommandManager.InvalidateRequerySuggested();
    }

    private void LoadEditableState()
    {
        var slot = Slot;
        KpText = DisplayUnits.Number(slot.Engineer.Kp, 3);
        KiText = DisplayUnits.Number(slot.Engineer.Ki, 3);
        CabinSetpointText = DisplayUnits.Number(DisplayUnits.ToFahrenheit(slot.Driver.CabinTemperatureSetpointCelsius));
        RequestedSpeedText = DisplayUnits.Number(DisplayUnits.ToMph(slot.Driver.RequestedSpeedMetersPerSecond));
        RequestedSpeedMessage = new DisplayValue(string.Empty);
        AnnouncementText = string.Empty;
        GainsMessage = string.Empty;
        InputMessage = string.Empty;

        OnPropertyChanged(nameof(SelectedTrainId));
        OnPropertyChanged(nameof(IsManual));
        OnPropertyChanged(nameof(IsAutomatic));
        OnPropertyChanged(nameof(ServiceBrakeRequested));
        OnPropertyChanged(nameof(LeftDoorsOpenRequested));
        OnPropertyChanged(nameof(RightDoorsOpenRequested));
        OnPropertyChanged(nameof(ExteriorLightsRequested));
    }

    /// <summary>Validates and applies Kp/Ki to the selected train only. Invalid input leaves the gains unchanged.</summary>
    public void ApplyGains()
    {
        if (!DisplayUnits.TryParseNumber(KpText, out var kp) || !DisplayUnits.TryParseNumber(KiText, out var ki))
        {
            GainsMessage = "Kp and Ki must be numbers.";
            return;
        }

        try
        {
            Slot.SetEngineerSettings(kp, ki);
            GainsMessage = $"Applied to {SelectedTrainId}.";
        }
        catch (ArgumentOutOfRangeException)
        {
            GainsMessage = "Kp and Ki must be ≥ 0.";
        }

        Refresh();
    }

    /// <summary>
    /// Applies the requested-speed entry to the SELECTED train only. Invalid input (non-numeric,
    /// negative) is rejected and leaves the stored value unchanged. Ignored in Automatic mode,
    /// where the controller does not use the driver request.
    /// </summary>
    public void ApplyRequestedSpeed()
    {
        if (!IsManual)
        {
            RequestedSpeedMessage = new DisplayValue("Automatic mode: the controller sets the target speed.", DisplayTone.Muted);
            return;
        }

        var entry = RequestedSpeedEntry.Parse(RequestedSpeedText, _subsystem.Options.Vehicle);
        RequestedSpeedMessage = entry.Message;

        if (entry.IsAccepted)
        {
            Slot.Driver.RequestedSpeedMetersPerSecond = entry.MetersPerSecond;
            RequestedSpeedText = DisplayUnits.Number(DisplayUnits.ToMph(entry.MetersPerSecond));
            Refresh();
        }
    }

    public void Announce()
    {
        if (string.IsNullOrWhiteSpace(AnnouncementText))
        {
            return;
        }

        Slot.Driver.RequestAnnouncement(AnnouncementText);
        AnnouncementText = string.Empty;
        Refresh();
    }

    public void ApplyCabinSetpoint()
    {
        if (!DisplayUnits.TryParseNumber(CabinSetpointText, out var fahrenheit))
        {
            InputMessage = "Cabin temperature must be a number (°F).";
            return;
        }

        Slot.Driver.CabinTemperatureSetpointCelsius = DisplayUnits.FromFahrenheit(fahrenheit);
        InputMessage = string.Empty;
    }

    private void RefreshEventLog()
    {
        if (_subsystem.Log is not InMemoryTrainControllerEventLog log)
        {
            return;
        }

        var entries = log.Snapshot();
        var newest = entries.Count > 0 ? entries[^1] : null;
        if (ReferenceEquals(newest, _lastLogEntry))
        {
            return;
        }

        _lastLogEntry = newest;
        EventLog.Clear();
        for (var i = entries.Count - 1; i >= 0 && EventLog.Count < 200; i--)
        {
            EventLog.Add(entries[i]); // newest first
        }
    }
}
