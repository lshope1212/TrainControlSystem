using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using TrainControl.Common.Utilities;
using TrainModel.Core.Interfaces;
using TrainModel.Core.Models;
using TrainModel.Core.Services;
using TrainModel.Core.Simulation;
using TrainModel.Wpf.Commands;
using TrainModel.Wpf.Services;

namespace TrainModel.Wpf.ViewModels;

/// <summary>
/// Train Model Console view model. Owns the multi-train fleet and the shared clock,
/// steps every train each WPF tick, and binds all panels to the SELECTED train
/// (the roster selection drives the whole console). Metric backend, imperial display.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly ITrainFleetService _fleet;
    private readonly SimulationClock _clock;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, TrainInputs> _inputsByTrain = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastFrameUtc;
    private readonly TrainModelOutputPublisher _publisher = new();
    private DateTime _lastPublishUtc = DateTime.MinValue;
    private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(250);

    public MainWindowViewModel()
        : this(new TrainFleetService())
    {
    }

    public MainWindowViewModel(ITrainFleetService fleet)
    {
        _fleet = fleet ?? throw new ArgumentNullException(nameof(fleet));
        _clock = new SimulationClock(dtSeconds: 0.1);

        BuildInterfaceRows();

        PlayPauseCommand = new RelayCommand(_ => TogglePlayPause());
        ResetCommand = new RelayCommand(_ => ResetSimulation());
        ToggleEngineFailureCommand = new RelayCommand(_ => ToggleFault("engine"));
        ToggleBrakeFailureCommand = new RelayCommand(_ => ToggleFault("brake"));
        ToggleSignalFailureCommand = new RelayCommand(_ => ToggleFault("signal"));
        PassengerEmergencyBrakeCommand = new RelayCommand(_ => RequestPassengerEBrake());
        ExchangePassengersCommand = new RelayCommand(_ => ExchangePassengers());

        SeedDemoTrains();

        _lastFrameUtc = DateTime.UtcNow;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += OnTick;
        _timer.Start();

        Log("INFO", "Console", "Train Model Console started.");
        RefreshAll();
    }

    // ---------------- Fleet seeding ----------------

    private void SeedDemoTrains()
    {
        AddSeededTrain("T04", "Red Line", 400, 19.44, TrainOperatingStatus.Operating, powerW: 0);
        AddSeededTrain("T08", "Green Line", 820, 17.0, TrainOperatingStatus.Operating, powerW: 60000);
        AddSeededTrain("T12", "Green Line", 680, 0.0, TrainOperatingStatus.Holding, powerW: 0);
        SelectedTrainId = "T04";
    }

    private void AddSeededTrain(string id, string line, double posM, double speedMs,
                                TrainOperatingStatus status, double powerW)
    {
        var train = new Train
        {
            Id = id,
            LineName = line,
            OperatingStatus = status,
            PositionMeters = posM,
            VelocityMetersPerSecond = speedMs,
        };
        train.Passengers.PassengerCount = 24;
        _fleet.AddTrain(train);

        _inputsByTrain[id] = new TrainInputs
        {
            PowerCommandWatts = powerW,
            CommandedSpeedMetersPerSecond = 19.44,
            CommandedAuthorityMeters = 381.0,
            CabinSetpointCelsius = 22.0,
        };

        Roster.Add(new TrainRosterItemViewModel(id));
    }

    // ---------------- Simulation loop ----------------

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double realSeconds = (now - _lastFrameUtc).TotalSeconds;
        _lastFrameUtc = now;

        int steps = _clock.StepsFor(realSeconds);
        for (int i = 0; i < steps; i++)
        {
            foreach (var kv in _inputsByTrain)
            {
                _fleet.ApplyInputs(kv.Key, kv.Value);
            }
            _fleet.StepAll(TimeSpan.FromSeconds(_clock.DtSeconds));

            // One-shot signals consumed after a step: passenger request latches to
            // pending in the service; controller approve/deny are applied once.
            foreach (var inp in _inputsByTrain.Values)
            {
                inp.PassengerEmergencyBrakeRequest = false;
                inp.ControllerApproveEmergencyBrake = false;
                inp.ControllerDenyEmergencyBrake = false;
            }
        }

        if (steps > 0)
        {
            RefreshAll();
            PublishOutputsThrottled();
        }
    }

    private void PublishOutputsThrottled()
    {
        var now = DateTime.UtcNow;
        if (now - _lastPublishUtc < PublishInterval) return;
        _lastPublishUtc = now;

        foreach (var plant in _fleet.Trains)
        {
            _publisher.Publish(plant.CurrentState);
        }
    }

    private void TogglePlayPause()
    {
        if (_clock.IsPaused) { _lastFrameUtc = DateTime.UtcNow; _clock.Resume(); }
        else { _clock.Pause(); }
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(PlayPauseLabel));
        Log("INFO", "Console", _clock.IsPaused ? "Simulation paused." : "Simulation resumed.");
    }

    private void ResetSimulation()
    {
        // Reset ONLY the selected train; the simulation clock and other trains keep
        // running. (A full-simulation/clock reset is available from the Test UI.)
        if (SelectedPlant is not { } plant) return;

        plant.Train.PositionMeters = 0;
        plant.Train.VelocityMetersPerSecond = 0;
        plant.Train.AccelerationMetersPerSecondSquared = 0;
        plant.Train.PreviousAccelerationMetersPerSecondSquared = 0;
        plant.ClearEmergencyBrakeFromController();
        // Clear any pending passenger request and the latched engaged state for this train.
        plant.Train.PassengerEmergencyBrakePending = false;

        if (SelectedInputs is { } inp)
        {
            inp.EngineFailure = inp.BrakeFailure = inp.SignalPickupFailure = false;
            inp.PassengerEmergencyBrakeRequest = false;
            inp.ControllerApproveEmergencyBrake = false;
            inp.ControllerDenyEmergencyBrake = false;
        }

        Log("INFO", "Console", $"{SelectedTrainId} reset.");
        RefreshAll();
    }

    // ---------------- Selected-train helpers ----------------

    private string _selectedTrainId = "T04";
    public string SelectedTrainId
    {
        get => _selectedTrainId;
        set
        {
            if (SetProperty(ref _selectedTrainId, value))
            {
                foreach (var r in Roster) r.IsSelected = r.TrainId == value;
                RefreshAll();
            }
        }
    }

    private ITrainSimulationService? SelectedPlant => _fleet.GetTrain(SelectedTrainId);
    private TrainInputs? SelectedInputs =>
        _inputsByTrain.TryGetValue(SelectedTrainId, out var i) ? i : null;

    private void ToggleFault(string which)
    {
        if (SelectedInputs is not { } inp) return;
        switch (which)
        {
            case "engine": inp.EngineFailure = !inp.EngineFailure;
                Log(inp.EngineFailure ? "WARN" : "INFO", "Murphy",
                    $"{SelectedTrainId} engine failure {(inp.EngineFailure ? "injected" : "cleared")}."); break;
            case "brake": inp.BrakeFailure = !inp.BrakeFailure;
                Log(inp.BrakeFailure ? "WARN" : "INFO", "Murphy",
                    $"{SelectedTrainId} brake failure {(inp.BrakeFailure ? "injected" : "cleared")}."); break;
            case "signal": inp.SignalPickupFailure = !inp.SignalPickupFailure;
                Log(inp.SignalPickupFailure ? "WARN" : "INFO", "Murphy",
                    $"{SelectedTrainId} signal pickup failure {(inp.SignalPickupFailure ? "injected" : "cleared")}."); break;
        }
        RefreshAll();
    }

    private void RequestPassengerEBrake()
    {
        if (SelectedInputs is not { } inp || SelectedPlant is not { } plant) return;
        // One request at a time, PER TRAIN: block another request for THIS train while
        // one is already pending or engaged. This does not affect other trains — each
        // tracks its own pending state, so T04 being pending never locks T08/T12.
        if (plant.Train.PassengerEmergencyBrakePending || plant.Train.EmergencyBrakeEngaged)
        {
            Log("INFO", "Passenger", $"{SelectedTrainId} already has a pending/active emergency brake; request ignored.");
            return;
        }
        inp.PassengerEmergencyBrakeRequest = true;
        Log("ALERT", "Passenger", $"{SelectedTrainId} passenger emergency brake requested (awaiting controller approval).");
        RefreshAll();
    }

    // ================= INBOUND (pipe) APPLICATION =================
    // Called by the named-pipe receiver on the UI thread. Each method folds a
    // received contract message into the target train's input vector, creating
    // (dispatching) the train if it is not yet on the railway. Routing is by TrainId.

    private TrainInputs EnsureTrain(string trainId, string lineName = "Red Line")
    {
        if (string.IsNullOrWhiteSpace(trainId)) trainId = "TRAIN-001";
        if (_inputsByTrain.TryGetValue(trainId, out var existing))
        {
            return existing;
        }

        // Dispatch a new train onto the railway on first message.
        var train = new Train
        {
            Id = trainId,
            LineName = lineName,
            OperatingStatus = TrainOperatingStatus.Operating,
        };
        _fleet.AddTrain(train);

        var inputs = new TrainInputs();
        _inputsByTrain[trainId] = inputs;
        Roster.Add(new TrainRosterItemViewModel(trainId));
        Log("INFO", "Dispatch", $"{trainId} dispatched onto the railway (first message received).");
        return inputs;
    }

    public void ApplyTrainCommand(string trainId, double powerWatts, bool serviceBrake,
        bool emergencyBrake, bool leftDoor, bool rightDoor, bool lights, double cabinCelsius,
        string announcement)
    {
        var inp = EnsureTrain(trainId);

        // Detect a change in the controller's DIRECT emergency-brake command so we can
        // log its application/release distinctly (not on every repeated command tick).
        bool wasEmergency = inp.EmergencyBrakeApplied;

        inp.PowerCommandWatts = powerWatts;
        inp.ServiceBrakeApplied = serviceBrake;
        inp.EmergencyBrakeApplied = emergencyBrake;
        inp.LeftDoorCommandOpen = leftDoor;
        inp.RightDoorCommandOpen = rightDoor;
        inp.ExteriorLightCommandOn = lights;
        inp.CabinSetpointCelsius = cabinCelsius;
        inp.StationAnnouncement = announcement;

        Log("INFO", "Controller", $"{trainId} command received (power {powerWatts/745.7:0.0} hp).");
        if (emergencyBrake && !wasEmergency)
            Log("ALERT", "Controller", $"{trainId} EMERGENCY BRAKE applied by controller.");
        else if (!emergencyBrake && wasEmergency)
            Log("INFO", "Controller", $"{trainId} emergency brake released by controller.");

        RefreshAll();
    }

    public void ApplyCommandedSpeed(string trainId, double metersPerSecond)
    {
        EnsureTrain(trainId).CommandedSpeedMetersPerSecond = metersPerSecond;
        RefreshAll();
    }

    public void ApplyCommandedAuthority(string trainId, double meters)
    {
        EnsureTrain(trainId).CommandedAuthorityMeters = meters;
        RefreshAll();
    }

    public void ApplyTrackCondition(string trainId, double gradePercent)
    {
        var inp = EnsureTrain(trainId);
        inp.GradePercent = gradePercent;
        RefreshAll();
    }

    public void ApplyBeacon(string trainId, string station, double distanceMeters, string side, string announcement)
    {
        var inp = EnsureTrain(trainId);
        inp.Beacon = new TrainControl.Contracts.Messages.BeaconMessage
        {
            TrainId = trainId,
            StationName = station,
            DistanceToStopMeters = distanceMeters,
            Side = side switch
            {
                "Left" => TrainControl.Contracts.Messages.PlatformSide.Left,
                "Right" => TrainControl.Contracts.Messages.PlatformSide.Right,
                _ => TrainControl.Contracts.Messages.PlatformSide.None,
            },
            Announcement = announcement,
        };
        Log("INFO", "Track", $"{trainId} beacon received: {station}.");
        RefreshAll();
    }

    public void ApplyPassengerDemand(string trainId, int waiting)
    {
        EnsureTrain(trainId).PassengerDemand = waiting;
        RefreshAll();
    }

    public void ApplyFailures(string trainId, bool engine, bool brake, bool signal)
    {
        var inp = EnsureTrain(trainId);
        inp.EngineFailure = engine;
        inp.BrakeFailure = brake;
        inp.SignalPickupFailure = signal;
        Log(engine || brake || signal ? "WARN" : "INFO", "Murphy",
            $"{trainId} failure state applied (engine {engine}, brake {brake}, signal {signal}).");
        RefreshAll();
    }

    public void ApplyPassengerEmergencyBrake(string trainId, bool requested)
    {
        var inp = EnsureTrain(trainId);
        // Raises a PENDING request only — the controller must approve before it engages.
        inp.PassengerEmergencyBrakeRequest = requested;
        Log("ALERT", "Passenger", $"{trainId} passenger emergency brake requested (awaiting controller approval).");
        RefreshAll();
    }

    public void ApplyEmergencyBrakeApproval(string trainId, bool approved)
    {
        var inp = EnsureTrain(trainId);

        // Is there actually a pending request for this train? The ruling only takes
        // effect if so (the plant guards this too); otherwise it is a no-op and we say so.
        bool pending = _fleet.GetTrain(trainId) is { } plant && plant.Train.PassengerEmergencyBrakePending;

        if (!pending)
        {
            // No request to rule on — do not set approve/deny flags; report it clearly.
            Log("INFO", "Controller",
                $"{trainId}: emergency brake {(approved ? "approval" : "denial")} received with NO pending request — brake not activated.");
            RefreshAll();
            return;
        }

        inp.ControllerApproveEmergencyBrake = approved;
        inp.ControllerDenyEmergencyBrake = !approved;
        Log(approved ? "ALERT" : "INFO", "Controller",
            $"{trainId} emergency brake request {(approved ? "APPROVED (engaging)" : "DENIED")}.");
        RefreshAll();
    }

    /// <summary>Surface a pipe receive/routing status line into the Event Log.</summary>
    public void ReportPipeStatus(string message) => Log("INFO", "Pipe", message);

    private int _alightingCount;
    /// <summary>Number of passengers to alight at the next exchange (entered on the console).</summary>
    public string AlightingCount
    {
        get => _alightingCount.ToString();
        set { _alightingCount = int.TryParse(value, out var n) && n >= 0 ? n : 0; OnPropertyChanged(); }
    }

    private string _exchangeResult = "No exchange yet.";
    public string ExchangeResult { get => _exchangeResult; private set => SetProperty(ref _exchangeResult, value); }

    /// <summary>Boarded / alighted result for the selected train (shown on the console).</summary>
    public string BoardedAlightedDisplay => Fmt(s => s.WasAtCapacity
        ? $"{s.LastBoarded} boarded / {s.LastAlighted} alighted  ({s.LastLeftBehind} left — FULL)"
        : $"{s.LastBoarded} boarded / {s.LastAlighted} alighted");

    /// <summary>Run a station passenger exchange on the selected train: alight the entered
    /// count, board from that train's current Track-Model passenger demand (capped by
    /// capacity). The Train Model decides who alights; boarding is limited by demand.</summary>
    private void ExchangePassengers()
    {
        if (SelectedPlant is not { } plant) { ExchangeResult = "No train selected."; return; }
        int waiting = SelectedInputs?.PassengerDemand ?? 0;
        var (boarded, alighted) = plant.ExchangePassengers(waiting, _alightingCount);
        ExchangeResult = $"{SelectedTrainId}: {boarded} boarded / {alighted} alighted" +
            (plant.Train.Passengers.WasAtCapacity ? $" ({plant.Train.Passengers.LastLeftBehind} left — FULL)" : "");
        Log("INFO", "Passenger", $"{SelectedTrainId} exchange: {boarded} on / {alighted} off.");
        OnPropertyChanged(nameof(BoardedAlightedDisplay));
        OnPropertyChanged(nameof(OnboardLoadDisplay));
    }

    // ---------------- Clock / header ----------------

    // ---------------- Clock / header ----------------

    public double SpeedMultiplier
    {
        get => _clock.SpeedMultiplier;
        set { _clock.SetSpeed(Math.Clamp(value, 1.0, 10.0)); OnPropertyChanged(); OnPropertyChanged(nameof(SpeedMultiplierDisplay)); }
    }
    public string SpeedMultiplierDisplay => $"{SpeedMultiplier:0.#}x";
    public bool IsRunning => !_clock.IsPaused;
    public string PlayPauseLabel => _clock.IsPaused ? "Run" : "Pause";
    public string ElapsedDisplay => _clock.Elapsed.ToString(@"hh\:mm\:ss");

    public ICommand PlayPauseCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand ToggleEngineFailureCommand { get; }
    public ICommand ToggleBrakeFailureCommand { get; }
    public ICommand ToggleSignalFailureCommand { get; }
    public ICommand PassengerEmergencyBrakeCommand { get; }
    public ICommand ExchangePassengersCommand { get; }

    // ---------------- Collections bound by the view ----------------

    public ObservableCollection<TrainRosterItemViewModel> Roster { get; } = new();
    public ObservableCollection<EventLogItemViewModel> EventLog { get; } = new();

    public ObservableCollection<IoRowViewModel> TrackRows { get; } = new();
    public ObservableCollection<IoRowViewModel> ControllerRows { get; } = new();

    private void BuildInterfaceRows()
    {
        // Track Model interface (matches wireframe order)
        TrackRows.Add(new IoRowViewModel("Commanded Speed", "INPUT", "TRACK · IN"));
        TrackRows.Add(new IoRowViewModel("Comm. Authority", "INPUT", "TRACK · IN"));
        TrackRows.Add(new IoRowViewModel("Track Signal Status", "INPUT", "TRACK · IN"));
        TrackRows.Add(new IoRowViewModel("Grade", "INPUT", "TRACK · IN"));
        TrackRows.Add(new IoRowViewModel("Passenger Demand", "INPUT", "TRACK · IN"));
        TrackRows.Add(new IoRowViewModel("Actual Speed", "OUTPUT", "TRAIN · OUT"));
        TrackRows.Add(new IoRowViewModel("Position", "OUTPUT", "TRAIN · OUT"));

        // Train Controller interface
        ControllerRows.Add(new IoRowViewModel("Power Command", "INPUT", "CONTROLLER · IN"));
        ControllerRows.Add(new IoRowViewModel("Service Brake Cmd", "INPUT", "CONTROLLER · IN"));
        ControllerRows.Add(new IoRowViewModel("Emergency Brake Cmd", "INPUT", "CONTROLLER · IN"));
        ControllerRows.Add(new IoRowViewModel("Left / Right Door Cmd", "INPUT", "CONTROLLER · IN"));
        ControllerRows.Add(new IoRowViewModel("Exterior Lights", "INPUT", "CONTROLLER · IN"));
        ControllerRows.Add(new IoRowViewModel("Target Cabin Temperature", "INPUT", "CONTROLLER · IN"));
        ControllerRows.Add(new IoRowViewModel("Actual Speed Feedback", "OUTPUT", "TRAIN · OUT"));
        ControllerRows.Add(new IoRowViewModel("Cabin Temp Status", "OUTPUT", "TRAIN · OUT"));
        ControllerRows.Add(new IoRowViewModel("Commanded Speed", "OUTPUT", "TRAIN · OUT"));
        ControllerRows.Add(new IoRowViewModel("Comm. Authority", "OUTPUT", "TRAIN · OUT"));
        ControllerRows.Add(new IoRowViewModel("Track Signal Status", "OUTPUT", "TRAIN · OUT"));
        ControllerRows.Add(new IoRowViewModel("Beacon Information", "OUTPUT", "TRAIN · OUT"));
        ControllerRows.Add(new IoRowViewModel("Passenger Emergency Brake", "OUTPUT", "TRAIN · OUT"));
        ControllerRows.Add(new IoRowViewModel("Cabin Temperature Status", "OUTPUT", "TRAIN · OUT"));
    }

    // ---------------- Telemetry (selected train, imperial) ----------------

    private TrainState? Sel => SelectedPlant?.CurrentState;

    public string ActualSpeedDisplay => Fmt(s => $"{Mph(s.VelocityMetersPerSecond):0.0} mph");
    public string CommandedSpeedDisplay => Fmt(s => $"{Mph(s.CommandedSpeedMetersPerSecond):0.0} mph");
    public string AuthorityDisplay => Fmt(s => $"{Ft(s.CommandedAuthorityMeters):0} ft");
    // Show the EFFECTIVE power the plant actually uses — clamped to the engine's
    // max power — not the raw commanded value. Commanding 100000 hp shows the cap.
    public string PowerCommandDisplay => (SelectedPlant is { } p)
        ? $"{Hp(Math.Clamp(SelectedInputs?.PowerCommandWatts ?? 0, 0, p.Train.Specs.MaxPowerWatts)):0.0} hp"
        : "--";
    public string CabinTempDisplay => Fmt(s => $"{Cf(s.CabinTemperatureCelsius):0.0} °F");
    public string PositionDisplay => Fmt(s => $"{Ft(s.FrontPositionMeters):0} ft");
    public string NextStationDisplay => Fmt(s => string.IsNullOrWhiteSpace(s.BeaconStationName) ? "--" : s.BeaconStationName);
    public string StationAnnouncementDisplay => Fmt(s => string.IsNullOrWhiteSpace(s.StationAnnouncement) ? "(no announcement)" : s.StationAnnouncement);
    public string BeaconDisplay => Fmt(s => string.IsNullOrWhiteSpace(s.BeaconStationName)
        ? "(no beacon data)"
        : $"{s.BeaconStationName}  ·  {Ft(s.BeaconDistanceToStopMeters):0} ft to stop  ·  platform {s.BeaconPlatformSide}");
    public string ServiceBrakeDisplay => Fmt(s => s.ServiceBrakeEngaged ? "Applied" : "Released");
    public string SelectedHeaderDisplay => $"{SelectedTrainId} · {Sel?.LineName ?? ""}";

    // ---------------- Config (selected train, imperial) ----------------

    private TrainSpecs? Specs => SelectedPlant?.Train.Specs;
    public string ConsistDisplay => Specs is { } sp ? $"{sp.Consist} cars" : "--";
    public string LengthDisplay => Specs is { } sp ? $"{Ft(sp.LengthMeters):0.0} ft" : "--";
    public string HeightDisplay => Specs is { } sp ? $"{Ft(sp.HeightMeters):0.0} ft" : "--";
    public string EmptyWeightDisplay => Specs is { } sp ? $"{Lb(sp.EmptyMassKg):N0} lbs" : "--";
    // Live total weight = empty consist + current passengers + crew (updates as passengers board/alight).
    public string TotalWeightDisplay => SelectedPlant is { } p ? $"{Lb(p.Train.TotalMassKg):N0} lbs" : "--";
    public string PerModuleWeightDisplay => Specs is { } sp ? $"{Lb(sp.EmptyMassKg / Math.Max(1, sp.Consist)):N0} lbs avg" : "--";
    public string MaxSpeedDisplay => Specs is { } sp ? $"{Mph(sp.MaxSpeedMetersPerSecond):0.0} mph" : "--";
    public string MaxPowerDisplay => Specs is { } sp ? $"{Hp(sp.MaxPowerWatts):0.0} hp" : "--";
    public string MaxAccelDisplay => Specs is { } sp ? $"{MphS(sp.MaxAccelMetersPerSecondSquared):0.00} mph/s" : "--";
    public string MaxDecelDisplay => Specs is { } sp ? $"{MphS(sp.ServiceDecelMetersPerSecondSquared):0.00} / {MphS(sp.EmergencyDecelMetersPerSecondSquared):0.00} mph/s" : "--";
    public string MaxPassengerDisplay => Specs is { } sp ? $"{sp.PassengerCapacity} ({sp.SeatedCapacity}+{sp.StandingCapacity})" : "--";
    // Config panel: MAX capacity + crew (constants from the datasheet).
    public string MaxCapacityCrewDisplay => Specs is { } sp ? $"{sp.PassengerCapacity} / {sp.CrewCount} crew" : "--";
    // Telemetry: CURRENT onboard load vs capacity (live).
    public string OnboardLoadDisplay => (Sel is { } s && Specs is { } sp) ? $"{s.PassengerCount} / {sp.PassengerCapacity}" : "--";

    // ---------------- Murphy pills (selected train) ----------------

    public string EngineStateDisplay => (SelectedInputs?.EngineFailure ?? false) ? "ENGINE: FAILED" : "ENGINE: IDLE";
    public string BrakeStateDisplay => (SelectedInputs?.BrakeFailure ?? false) ? "BRAKE: FAILED" : "BRAKE: STANDBY";
    public string SignalStateDisplay => (SelectedInputs?.SignalPickupFailure ?? false) ? "SIGNAL: FAILED" : "SIGNAL: STANDBY";
    public string PassengerEBrakeStateDisplay =>
        (Sel?.PassengerEmergencyBrakeActive ?? false) ? "ENGAGED"
        : (Sel?.PassengerEmergencyBrakePending ?? false) ? "PENDING APPROVAL"
        : "NORMAL";
    public string PassengerEBrakeRequestDisplay =>
        (Sel?.PassengerEmergencyBrakeActive ?? false) ? "Request State: Approved - brake engaged"
        : (Sel?.PassengerEmergencyBrakePending ?? false) ? "Request State: Pending controller approval"
        : "Request State: No Active Request";

    // ---- Color-state "kinds" for the view's DataTriggers ----
    // Failure boxes: "Active" (failed) vs "Idle" (grayed). Reset clears to Idle because
    // it clears the failure flags, so these recompute to Idle automatically.
    public string EngineStateKind => (SelectedInputs?.EngineFailure ?? false) ? "Active" : "Idle";
    public string BrakeStateKind  => (SelectedInputs?.BrakeFailure ?? false) ? "Active" : "Idle";
    public string SignalStateKind => (SelectedInputs?.SignalPickupFailure ?? false) ? "Active" : "Idle";

    // Passenger e-brake: "Engaged" | "Pending" | "Normal" (drives color of the status element).
    public string PassengerEBrakeKind =>
        (Sel?.PassengerEmergencyBrakeActive ?? false) ? "Engaged"
        : (Sel?.PassengerEmergencyBrakePending ?? false) ? "Pending"
        : "Normal";

    // Can a new passenger request be sent for the selected train? (false while pending/engaged)
    public bool CanRequestPassengerEBrake =>
        !(Sel?.PassengerEmergencyBrakeActive ?? false) && !(Sel?.PassengerEmergencyBrakePending ?? false);

    // Service brake: "Applied" vs "Released" for color.
    public string ServiceBrakeKind => (Sel?.ServiceBrakeEngaged ?? false) ? "Applied" : "Released";

    // ---------------- Unit helpers ----------------

    private static double Mph(double ms) => UnitConversion.MetersPerSecondToMilesPerHour(ms);
    private static double Ft(double m) => UnitConversion.MetersToFeet(m);
    private static double Hp(double w) => UnitConversion.WattsToHorsepower(w);
    private static double Lb(double kg) => UnitConversion.KilogramsToPounds(kg);
    private static double Cf(double c) => UnitConversion.CelsiusToFahrenheit(c);
    private static double MphS(double a) => UnitConversion.MetersPerSecondSquaredToMphPerSecond(a);
    private string Fmt(Func<TrainState, string> f) => Sel is { } s ? f(s) : "--";

    // ---------------- Event log ----------------

    private void Log(string severity, string source, string message)
    {
        EventLog.Insert(0, new EventLogItemViewModel { Severity = severity, Source = source, Message = message });
        while (EventLog.Count > 50) EventLog.RemoveAt(EventLog.Count - 1);
    }

    // ---------------- Refresh ----------------

    private void RefreshAll()
    {
        // roster rows
        foreach (var plant in _fleet.Trains)
        {
            var row = Roster.FirstOrDefault(r => r.TrainId == plant.Train.Id);
            row?.Update(plant.CurrentState);
        }

        // selected-train interface rows
        if (Sel is { } s)
        {
            var inp = SelectedInputs;
            // Null-safe engine max power for clamping the displayed power command.
            double maxPowerW = SelectedPlant?.Train.Specs.MaxPowerWatts ?? 0.0;
            SetRow(TrackRows, "Commanded Speed", $"{Mph(s.CommandedSpeedMetersPerSecond):0.0} mph");
            SetRow(TrackRows, "Comm. Authority", $"{Ft(s.CommandedAuthorityMeters):0} ft");
            SetRow(TrackRows, "Track Signal Status", s.TrackSignal == SignalAvailability.Acquired ? "SIGNAL ACQUIRED" : "SIGNAL LOST");
            SetRow(TrackRows, "Grade", $"{s.GradePercent:0.0} %");
            SetRow(TrackRows, "Passenger Demand", $"{inp?.PassengerDemand ?? 0} Boarding");
            SetRow(TrackRows, "Actual Speed", $"{Mph(s.VelocityMetersPerSecond):0.0} mph");
            SetRow(TrackRows, "Position", $"{Ft(s.FrontPositionMeters):0} ft");

            SetRow(ControllerRows, "Power Command",
                $"{Hp(Math.Clamp(inp?.PowerCommandWatts ?? 0, 0, maxPowerW)):0.0} hp");
            SetRow(ControllerRows, "Service Brake Cmd", s.ServiceBrakeEngaged ? "APPLIED" : "RELEASED");
            SetRow(ControllerRows, "Emergency Brake Cmd", s.EmergencyBrakeEngaged ? "APPLIED" : "RELEASED");
            SetRow(ControllerRows, "Left / Right Door Cmd", $"{(s.LeftDoorOpen ? "OPEN" : "CLOSED")} / {(s.RightDoorOpen ? "OPEN" : "CLOSED")}");
            SetRow(ControllerRows, "Exterior Lights", s.ExteriorLightsOn ? "ON" : "OFF");
            SetRow(ControllerRows, "Target Cabin Temperature", $"{Cf(inp?.CabinSetpointCelsius ?? 21):0.0} °F");
            SetRow(ControllerRows, "Actual Speed Feedback", $"{Mph(s.VelocityMetersPerSecond):0.0} mph");
            SetRow(ControllerRows, "Cabin Temp Status", $"{Cf(s.CabinTemperatureCelsius):0.0} °F");
            SetRow(ControllerRows, "Commanded Speed", $"{Mph(s.CommandedSpeedMetersPerSecond):0.0} mph");
            SetRow(ControllerRows, "Comm. Authority", $"{Ft(s.CommandedAuthorityMeters):0} ft");
            SetRow(ControllerRows, "Track Signal Status", s.TrackSignal == SignalAvailability.Acquired ? "SIGNAL ACQUIRED" : "SIGNAL LOST");
            SetRow(ControllerRows, "Beacon Information", string.IsNullOrWhiteSpace(s.BeaconStationName)
                ? "--" : $"Station: {s.BeaconStationName}");
            SetRow(ControllerRows, "Passenger Emergency Brake", s.PassengerEmergencyBrakeActive ? "ACTIVE" : "RELEASED");
            SetRow(ControllerRows, "Cabin Temperature Status", $"{Cf(s.CabinTemperatureCelsius):0.0} °F");
        }

        // scalar telemetry + murphy
        foreach (var n in new[]
        {
            nameof(ActualSpeedDisplay), nameof(CommandedSpeedDisplay), nameof(AuthorityDisplay),
            nameof(PowerCommandDisplay), nameof(CabinTempDisplay), nameof(PositionDisplay),
            nameof(ServiceBrakeDisplay), nameof(SelectedHeaderDisplay),
            nameof(NextStationDisplay), nameof(StationAnnouncementDisplay), nameof(BeaconDisplay),
            nameof(BoardedAlightedDisplay), nameof(ExchangeResult),
            nameof(ConsistDisplay), nameof(LengthDisplay), nameof(HeightDisplay), nameof(EmptyWeightDisplay), nameof(TotalWeightDisplay),
            nameof(PerModuleWeightDisplay), nameof(MaxSpeedDisplay), nameof(MaxPowerDisplay), nameof(MaxAccelDisplay),
            nameof(MaxDecelDisplay), nameof(MaxPassengerDisplay), nameof(MaxCapacityCrewDisplay), nameof(OnboardLoadDisplay),
            nameof(EngineStateDisplay), nameof(BrakeStateDisplay), nameof(SignalStateDisplay),
            nameof(PassengerEBrakeStateDisplay), nameof(PassengerEBrakeRequestDisplay),
            nameof(EngineStateKind), nameof(BrakeStateKind), nameof(SignalStateKind),
            nameof(PassengerEBrakeKind), nameof(CanRequestPassengerEBrake), nameof(ServiceBrakeKind),
            nameof(ElapsedDisplay), nameof(IsRunning), nameof(PlayPauseLabel),
        })
        {
            OnPropertyChanged(n);
        }
    }

    private static void SetRow(ObservableCollection<IoRowViewModel> rows, string label, string value)
    {
        var r = rows.FirstOrDefault(x => x.Label == label);
        if (r is not null) r.Value = value;
    }
}
