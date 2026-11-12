using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using TrainControl.Common.Identity;
using TrainControl.Common.Utilities;
using TrainControl.Contracts.Messages;
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
    private readonly TrackCircuitReceiver _trackCircuit = new();

    // Last controller TickId received per train, echoed back on that train's status message
    // so the Train Controller can correlate its command (tick N) with the resulting state.
    private readonly Dictionary<string, long> _lastControllerTickByTrain = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastFrameUtc;
    private readonly TrainModelOutputPublisher _publisher;

    // ---- Timing (state-driven link to the Train Controller) ----
    // Every 0.1 s physics step is numbered (_stepCount = the TickId on the controller status).
    // Steps follow the shared system clock (SystemTimeMessage from the Launcher/CTC or the
    // TestUI test clock) while it is being received; otherwise the local clock runs them.
    // Due system-clock steps are PACED over real time at the clock's speed instead of being
    // run in one burst, so the controller's answers arrive between steps.
    private static readonly TimeSpan SystemClockTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);
    private readonly SystemTimeStepper _stepper = new();
    private long _stepCount;
    private DateTime _lastSystemTimeUtc = DateTime.MinValue;
    private double _systemSpeed = 1.0;
    private bool _systemPaused;
    private int _pendingSystemSteps;
    private double _paceCarry;
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private bool _refreshDirty;

    // Trains currently dispatched (on the track). Only these are stepped and reported to the
    // Train Controller with IsActive = true; a removed train is reported once with IsActive =
    // false so the controller stops controlling it.
    private readonly HashSet<string> _activeTrains = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _beaconSequence = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _lastBeaconRead = new(StringComparer.OrdinalIgnoreCase);

    // The Track Model dispatch each active train runs under (TrainActivationMessage). Its position
    // reports carry it, so the Track Model never applies one dispatch's telemetry to another.
    private readonly Dictionary<string, string> _dispatchRequestByTrain = new(StringComparer.OrdinalIgnoreCase);

    public MainWindowViewModel()
        : this(new TrainFleetService())
    {
    }

    /// <param name="publisher">Output endpoints; the production pipes unless a test isolates them.</param>
    public MainWindowViewModel(ITrainFleetService fleet, TrainModelOutputPublisher? publisher = null)
    {
        _fleet = fleet ?? throw new ArgumentNullException(nameof(fleet));
        _publisher = publisher ?? new TrainModelOutputPublisher();
        _clock = new SimulationClock(dtSeconds: 0.1);

        BuildInterfaceRows();

        PlayPauseCommand = new RelayCommand(_ => TogglePlayPause());
        ResetCommand = new RelayCommand(_ => ResetSimulation());
        ToggleEngineFailureCommand = new RelayCommand(_ => ToggleFault("engine"));
        ToggleBrakeFailureCommand = new RelayCommand(_ => ToggleFault("brake"));
        ToggleSignalFailureCommand = new RelayCommand(_ => ToggleFault("signal"));
        PassengerEmergencyBrakeCommand = new RelayCommand(_ => RequestPassengerEBrake());
        ExchangePassengersCommand = new RelayCommand(_ => ExchangePassengers());

        // No demo trains and a PAUSED local clock on startup: in the full system trains appear
        // only when they are dispatched (TrackModelTrainUpdateMessage), and time comes from the
        // shared system clock. The TestUI can dispatch demo trains for standalone testing.
        _clock.Pause();

        _lastFrameUtc = DateTime.UtcNow;
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(15) };
        _timer.Tick += OnTick;
        _timer.Start();

        Log("INFO", "Console", "Train Model Console started.");
        RefreshAll();
    }

    // ---------------- Fleet seeding ----------------

    private void SeedDemoTrains()
    {
        // Canonical three-digit train IDs (TrainIdConvention / CTC dispatch IDs).
        AddSeededTrain("001", "Red Line", 400, 19.44, TrainOperatingStatus.Operating, powerW: 0);
        AddSeededTrain("002", "Green Line", 820, 17.0, TrainOperatingStatus.Operating, powerW: 60000);
        AddSeededTrain("003", "Green Line", 680, 0.0, TrainOperatingStatus.Holding, powerW: 0);
        SelectedTrainId = "001";
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

    private bool UsesSystemClock => DateTime.UtcNow - _lastSystemTimeUtc < SystemClockTimeout;

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double realSeconds = (now - _lastFrameUtc).TotalSeconds;
        _lastFrameUtc = now;

        int steps;
        if (UsesSystemClock)
        {
            // Pace the steps the system clock has made due: about speed / dt steps per real second.
            _paceCarry += _systemPaused ? 0.0 : realSeconds * _systemSpeed / _clock.DtSeconds;
            steps = Math.Min(_pendingSystemSteps, (int)_paceCarry);
            _pendingSystemSteps -= steps;
            _paceCarry -= steps;
            if (_pendingSystemSteps == 0)
            {
                _paceCarry = Math.Min(_paceCarry, 1.0); // no burst after an idle period
            }
        }
        else
        {
            steps = _clock.StepsFor(realSeconds);
        }

        for (int i = 0; i < steps; i++)
        {
            StepOnce();
        }

        if (steps > 0)
        {
            _refreshDirty = true;
        }

        if (_refreshDirty && now - _lastRefreshUtc >= RefreshInterval)
        {
            RefreshNow();
        }
    }

    /// <summary>
    /// One 0.1 s physics step for every train, then per train: one status to the controller and one
    /// position report (cumulative distance since dispatch, speed, length) to the Track Model.
    /// </summary>
    private void StepOnce()
    {
        // Only dispatched (active) trains move.
        var dt = TimeSpan.FromSeconds(_clock.DtSeconds);
        foreach (var kv in _inputsByTrain)
        {
            if (!_activeTrains.Contains(kv.Key)) continue;
            _fleet.ApplyInputs(kv.Key, kv.Value);
            var sim = _fleet.GetTrain(kv.Key);
            sim?.Step(dt);

            // If the step triggered an automatic station exchange, report it to the Track Model
            // once (passenger info is Train Model -> Track Model), then clear the one-shot flag.
            if (sim is { StationExchangeOccurred: true })
            {
                _publisher.PublishPassengerExchange(sim.CurrentState);
                sim.StationExchangeOccurred = false;
                Log("INFO", "Passenger",
                    $"{kv.Key} station exchange: {sim.CurrentState.LastBoarded} on / {sim.CurrentState.LastAlighted} off" +
                    (sim.CurrentState.LastLeftBehind > 0 ? $" ({sim.CurrentState.LastLeftBehind} left — FULL)" : "") + ".");
            }
        }

        // One-shot signals consumed after a step: passenger request latches to
        // pending in the service; controller approve/deny are applied once.
        foreach (var inp in _inputsByTrain.Values)
        {
            inp.PassengerEmergencyBrakeRequest = false;
            inp.ControllerApproveEmergencyBrake = false;
            inp.ControllerDenyEmergencyBrake = false;
        }

        _stepCount++;
        foreach (var plant in _fleet.Trains)
        {
            if (!_activeTrains.Contains(plant.Train.Id)) continue;
            var state = plant.CurrentState;
            // The pending/active passenger request from the train STATE (the input flag is a
            // one-shot cleared every step); it stays on until the controller's e-brake arrives.
            bool passengerRequested = state.PassengerEmergencyBrakePending || state.PassengerEmergencyBrakeActive;
            _publisher.PublishToController(state, _stepCount, isActive: true, passengerRequested,
                _beaconSequence.GetValueOrDefault(plant.Train.Id));
            PublishPosition(plant);
        }
    }

    /// <summary>The train's position report to the Track Model, tagged with the current physics tick.</summary>
    private void PublishPosition(ITrainSimulationService plant) =>
        _publisher.PublishPosition(plant.CurrentState, _stepCount,
            _dispatchRequestByTrain.TryGetValue(plant.Train.Id, out var dispatch) ? dispatch : string.Empty);

    /// <summary>Shared system clock update (Launcher/CTC or the TestUI test clock).</summary>
    public void ApplySystemTime(SystemTimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        bool connected = UsesSystemClock;
        _lastSystemTimeUtc = DateTime.UtcNow;
        _systemSpeed = Math.Clamp(message.SpeedMultiplier, 1.0, 10.0);
        _systemPaused = message.IsPaused;

        var advance = _stepper.Advance(message.SystemTime, _ => _pendingSystemSteps++);
        switch (advance.Kind)
        {
            case SystemTimeAdvanceKind.Baseline:
                if (!connected) Log("INFO", "Clock", "Following the shared system clock.");
                break;
            case SystemTimeAdvanceKind.MovedBackward:
                _pendingSystemSteps = 0;
                Log("INFO", "Clock", "System time moved back: counting fresh from the new time.");
                break;
            case SystemTimeAdvanceKind.JumpedForward:
                _pendingSystemSteps = 0;
                Log("WARN", "Clock", "System time jumped ahead more than 10 s: not simulating the gap.");
                break;
        }

        _refreshDirty = true;
    }

    private void TogglePlayPause()
    {
        if (UsesSystemClock)
        {
            Log("INFO", "Console", "The shared system clock is running this simulation: run/pause it from the CTC.");
            return;
        }

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

    private string _selectedTrainId = "001";
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
        if (string.IsNullOrWhiteSpace(trainId)) trainId = "001";
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
        Log("INFO", "Dispatch", $"{trainId} known (first message received); it runs once dispatched.");
        if (!TrainControl.Common.Identity.TrainIdConvention.TryNormalize(trainId, out var canonical)
            || !TrainControl.Common.Identity.TrainIdConvention.IsDispatchable(canonical))
        {
            // Diagnostic only: the train is still simulated, but no Train Controller drives it.
            Log("WARN", "Dispatch",
                $"{trainId} is not a supported fleet train ID ({TrainControl.Common.Identity.TrainIdConvention.DispatchableRangeText}); the Train Controller will ignore it.");
        }

        return inputs;
    }

    public void ApplyTrainCommand(string trainId, long tickId, double powerWatts, bool serviceBrake,
        bool emergencyBrake, bool leftDoor, bool rightDoor, bool lights, double cabinCelsius,
        string announcement)
    {
        var inp = EnsureTrain(trainId);

        // Detect a change in the emergency-brake command so we can log apply/release once.
        bool wasEmergency = inp.EmergencyBrakeApplied;

        // Remember the controller's tick for this train so our status message can echo it.
        _lastControllerTickByTrain[trainId] = tickId;

        inp.PowerCommandWatts = powerWatts;
        inp.ServiceBrakeApplied = serviceBrake;
        // Direct-apply emergency brake: the Train Controller is the sole authority. There is
        // no approval handshake — the brake follows the controller's EmergencyBrakeCommand.
        inp.EmergencyBrakeApplied = emergencyBrake;
        inp.LeftDoorCommandOpen = leftDoor;
        inp.RightDoorCommandOpen = rightDoor;
        inp.ExteriorLightCommandOn = lights;
        inp.CabinSetpointCelsius = cabinCelsius;
        inp.StationAnnouncement = announcement;

        if (emergencyBrake && !wasEmergency)
            Log("ALERT", "Controller", $"{trainId} EMERGENCY BRAKE applied by controller.");
        else if (!emergencyBrake && wasEmergency)
            Log("INFO", "Controller", $"{trainId} emergency brake released by controller.");

        RefreshAll();
    }

    /// <summary>
    /// TrackModelTrainUpdateMessage: a non-empty CurrentBlockId means the train is on the track
    /// (dispatched: created if needed and marked active); an empty one removes it from the track
    /// (marked inactive, reported once with IsActive = false, then no longer stepped).
    /// </summary>
    public void ApplyTrainPlacement(string trainId, string currentBlockId, double speedMetersPerSecond)
    {
        if (string.IsNullOrWhiteSpace(trainId)) return;
        trainId = trainId.Trim();
        if (string.IsNullOrWhiteSpace(currentBlockId))
        {
            _dispatchRequestByTrain.Remove(trainId);
            if (_activeTrains.Remove(trainId) && _fleet.GetTrain(trainId) is { } removed)
            {
                _publisher.PublishToController(removed.CurrentState, _stepCount, isActive: false, passengerRequested: false);
                Log("INFO", "Dispatch", $"{trainId} removed from the track (inactive).");
                RefreshAll();
            }

            return;
        }

        EnsureTrain(trainId);
        if (_activeTrains.Add(trainId))
        {
            if (_fleet.GetTrain(trainId) is { } plant && speedMetersPerSecond > 0.0)
            {
                plant.Train.VelocityMetersPerSecond = speedMetersPerSecond;
            }

            Log("INFO", "Dispatch", $"{trainId} dispatched onto block {currentBlockId.Trim()} (active).");
            RefreshAll();
        }
    }

    /// <summary>
    /// TrainActivationMessage from the Track Model (physical dispatch). Activate: the train is created
    /// if it does not exist, put at cumulative position 0 and at rest (its TrainSpecs, including its
    /// length, are kept), assigned its line for the roster, marked active, and its initial position report
    /// is published at once, even while the clock is paused, so the Track Model can occupy the start block.
    /// The Train Model never derives a track position from the start block.
    /// </summary>
    /// <remarks>
    /// Idempotent: repeating the activation of an active train with the same dispatch only re-publishes
    /// its current position. A different dispatch of an active train is adopted only while the train is
    /// still at its origin; a train that has moved is never reset by a retry. Activate = false retires it.
    /// </remarks>
    public void ApplyTrainActivation(TrainActivationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!TrainIdConvention.TryNormalize(message.TrainId, out var canonical))
        {
            Log("WARN", "Dispatch", $"Activation for '{message.TrainId}' ignored: not a canonical train ID.");
            return;
        }

        // The roster may know the train under its fleet label ("TRAIN-001"); translated here, never guessed.
        string trainId = _inputsByTrain.Keys.FirstOrDefault(key => TrainIdConvention.TryNormalize(key, out var known) && known == canonical) ?? canonical;
        string line = LineDisplayName(message.LineId);

        if (!message.Activate)
        {
            _dispatchRequestByTrain.Remove(trainId);
            if (_activeTrains.Remove(trainId) && _fleet.GetTrain(trainId) is { } retired)
            {
                _publisher.PublishToController(retired.CurrentState, _stepCount, isActive: false, passengerRequested: false);
                Log("INFO", "Dispatch", $"{trainId} retired by the Track Model ({(string.IsNullOrWhiteSpace(message.Reason) ? "no reason given" : message.Reason)}).");
                RefreshAll();
            }

            return;
        }

        if (_activeTrains.Contains(trainId) && _fleet.GetTrain(trainId) is { } active)
        {
            _dispatchRequestByTrain.TryGetValue(trainId, out var current);
            if (current == message.DispatchRequestId)
            {
                // A retry: nothing is reset; the current position is reported again.
                PublishPosition(active);
                return;
            }

            bool atOrigin = active.Train.PositionMeters == 0 && active.Train.VelocityMetersPerSecond == 0;
            if (!atOrigin)
            {
                Log("WARN", "Dispatch", $"{trainId}: activation for dispatch {message.DispatchRequestId} ignored: the train is operating under dispatch '{current}' and has travelled {active.Train.PositionMeters:0.0} m; a retry never resets an operating train.");
                return;
            }

            _dispatchRequestByTrain[trainId] = message.DispatchRequestId;
            active.Train.LineName = line;
            active.PlaceAtDispatchOrigin();
            Log("INFO", "Dispatch", $"{trainId} (still at its origin) now runs under dispatch {message.DispatchRequestId} at {message.StartBlockId}.");
            PublishPosition(active);
            RefreshAll();
            return;
        }

        var inputs = EnsureTrain(trainId, line);
        var plant = _fleet.GetTrain(trainId)!;
        plant.Train.LineName = line;
        plant.Train.OperatingStatus = TrainOperatingStatus.Operating;
        plant.PlaceAtDispatchOrigin();

        // No movement until the track codes it: a code or command left from an earlier run must not move the train.
        inputs.CommandedSpeedMetersPerSecond = 0;
        inputs.CommandedAuthorityMeters = 0;
        inputs.PowerCommandWatts = 0;

        _activeTrains.Add(trainId);
        _dispatchRequestByTrain[trainId] = message.DispatchRequestId;
        Log("INFO", "Dispatch", $"{trainId} activated by the Track Model at {message.StartBlockId} on the {line}: position 0 m, length {plant.Train.Specs.LengthMeters:0.0} m.");

        // The initial report: published now, not on the next step, so occupancy appears while paused.
        PublishPosition(plant);
        _publisher.PublishToController(plant.CurrentState, _stepCount, isActive: true, passengerRequested: false);
        RefreshAll();
    }

    /// <summary>"Green" -> "Green Line" for the roster (display only).</summary>
    private static string LineDisplayName(string lineId) =>
        string.IsNullOrWhiteSpace(lineId) ? "Unknown Line"
        : lineId.Trim().EndsWith(" Line", StringComparison.OrdinalIgnoreCase) ? lineId.Trim() : lineId.Trim() + " Line";

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

    /// <summary>
    /// Track-circuit data from the Track Model for the block a train occupies: commanded speed,
    /// authority (relayed unchanged, zero included) and grade. Applies only to trains in service
    /// here and only in snapshot order; environments of other blocks and trains are ignored.
    /// </summary>
    public void ApplyTrackEnvironment(TrainControl.Contracts.Messages.TrackModelTrainEnvironmentMessage environment)
    {
        if (string.IsNullOrEmpty(environment.TrainId))
        {
            return;
        }

        // The Track Model names trains by canonical ID ("001"); trains here may carry the Train
        // Controller's fleet label ("TRAIN-001"). Translated at this boundary, never guessed.
        var inputs = _inputsByTrain.TryGetValue(environment.TrainId, out var exact) ? exact
            : _inputsByTrain.FirstOrDefault(train => TrainControl.Common.Identity.TrainIdConvention.TryNormalize(train.Key, out var canonical) && canonical == environment.TrainId).Value;
        if (inputs is null)
        {
            return;
        }

        if (_trackCircuit.TryApply(environment.TrainId, environment, inputs))
        {
            RefreshAll();
        }
    }

    public void ApplyTrackCondition(string trainId, double gradePercent)
    {
        var inp = EnsureTrain(trainId);
        inp.GradePercent = gradePercent;
        RefreshAll();
    }

    public void ApplyBeacon(string trainId, string station, double distanceMeters, string side, string announcement, long readSequence = 0)
    {
        var inp = EnsureTrain(trainId);
        // A new beacon READ (new pass, or a changed beacon without a read id) bumps the sequence the
        // Train Controller uses to recalibrate its station distance; resends of one read do not.
        if (!string.IsNullOrEmpty(station))
        {
            var previous = inp.Beacon;
            _lastBeaconRead.TryGetValue(trainId, out var lastRead);
            bool isNewRead = readSequence > 0
                ? readSequence != lastRead
                : previous is null || previous.StationName != station || !previous.DistanceToStopMeters.Equals(distanceMeters);
            if (isNewRead)
            {
                _beaconSequence[trainId] = _beaconSequence.GetValueOrDefault(trainId) + 1;
            }

            _lastBeaconRead[trainId] = readSequence;
        }

        inp.Beacon = new TrainControl.Contracts.Messages.BeaconMessage
        {
            TrainId = trainId,
            StationName = station,
            DistanceToStopMeters = distanceMeters,
            Side = side switch
            {
                "Left" => TrainControl.Contracts.Enums.PlatformSide.Left,
                "Right" => TrainControl.Contracts.Enums.PlatformSide.Right,
                "Both" => TrainControl.Contracts.Enums.PlatformSide.Both,
                _ => TrainControl.Contracts.Enums.PlatformSide.None,
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
        // The passenger raises a REQUEST only. There is no local approval: the request is
        // reported to the Train Controller (in the status message) and the controller decides
        // whether to command the emergency brake. The Train Model never engages it from here.
        inp.PassengerEmergencyBrakeRequest = requested;
        Log("ALERT", "Passenger", $"{trainId} passenger emergency brake requested (reported to controller).");
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

        // Guard: a passenger exchange may only happen while the train is stopped at a station
        // (Derrick's point 4). "At a station" = effectively stopped and a station beacon is in
        // effect. This also applies to the manual button so it can't change counts mid-transit.
        bool stopped = plant.Train.VelocityMetersPerSecond < 0.05;
        bool atStation = !string.IsNullOrEmpty(SelectedInputs?.Beacon?.StationName);
        if (!stopped || !atStation)
        {
            ExchangeResult = "Exchange refused: train must be stopped at a station.";
            return;
        }

        int waiting = SelectedInputs?.PassengerDemand ?? 0;
        var (boarded, alighted) = plant.ExchangePassengers(waiting, _alightingCount);
        ExchangeResult = $"{SelectedTrainId}: {boarded} boarded / {alighted} alighted" +
            (plant.Train.Passengers.WasAtCapacity ? $" ({plant.Train.Passengers.LastLeftBehind} left — FULL)" : "");
        Log("INFO", "Passenger", $"{SelectedTrainId} exchange: {boarded} on / {alighted} off.");
        // Reported to the Track Model once per exchange (passenger information is Train Model -> Track Model).
        _publisher.PublishPassengerExchange(plant.CurrentState);
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
    public bool IsRunning => UsesSystemClock ? !_systemPaused : !_clock.IsPaused;
    public string PlayPauseLabel => UsesSystemClock ? (_systemPaused ? "Paused (CTC)" : "Running (CTC)") : (_clock.IsPaused ? "Run" : "Pause");
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

    /// <summary>Requests a UI refresh; it runs at most every <see cref="RefreshInterval"/>.</summary>
    private void RefreshAll()
    {
        if (DateTime.UtcNow - _lastRefreshUtc >= RefreshInterval)
        {
            RefreshNow();
        }
        else
        {
            _refreshDirty = true;
        }
    }

    private void RefreshNow()
    {
        _lastRefreshUtc = DateTime.UtcNow;
        _refreshDirty = false;
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
