using TrainControl.Contracts.Enums;
using TrainController.Abstractions.Configuration;
using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;
using TrainController.Abstractions.Validation;

namespace TrainController.Hardware.Pi.Controller;

/// <summary>
/// Hardware Train Controller for ONE train, executed on the Raspberry Pi. One call to
/// <see cref="Step"/> = one controller evaluation for one received request; the Pi never
/// advances time on its own.
/// </summary>
/// <remarks>
/// <para>
/// Independent implementation of the Train Controller requirements (see
/// src/TrainController/README.md) — it does NOT use TrainController.Core. It shares only the
/// data model, validation and the fail-safe builder from TrainController.Abstractions, so the
/// Software and Hardware controllers can be compared on identical test vectors.
/// </para>
/// <para>All runtime state below belongs to this single train; the Pi keeps one instance per Hardware train.</para>
/// </remarks>
public sealed class HardwareTrainController
{
    // ---- per-train runtime state -------------------------------------------------------
    private double _integral;                 // ∫ speed error dt (m)
    private EmergencyBrakeCause _latched;     // emergency-brake latch
    private bool _authorityKnown;
    private double _authorityLeft;            // remaining-authority estimate (m)
    private StationTarget? _station;          // current next-station target, null = none
    private readonly FailureWatch _watch = new FailureWatch(); // failure latches + detection windows
    private TrainControllerOutput? _previous;

    public HardwareTrainController(string trainId)
    {
        if (string.IsNullOrWhiteSpace(trainId))
        {
            throw new ArgumentException("TrainId is required.", nameof(trainId));
        }

        TrainId = trainId;
    }

    public string TrainId { get; }

    public double IntegralMeters => _integral;

    public EmergencyBrakeCause LatchedEmergencyCauses => _latched;

    public ControllerFailure LatchedFailures => _watch.Latched;

    public void Reset()
    {
        _integral = 0.0;
        _latched = EmergencyBrakeCause.None;
        _authorityKnown = false;
        _authorityLeft = 0.0;
        _station = null;
        _watch.Clear();
        _previous = null;
    }

    /// <exception cref="ArgumentException">Input for another train.</exception>
    public TrainControllerOutput Step(TrainControllerInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.TrainId != TrainId)
        {
            throw new ArgumentException($"Hardware controller for {TrainId} got input for {input.TrainId}.", nameof(input));
        }

        var check = TrainControllerInputValidator.Validate(input);
        if (!check.IsValid)
        {
            _integral = 0.0;
            _watch.NoCommand();
            _previous = FailSafeOutput.Create(TrainId, input.TickId, EmergencyBrakeCause.InvalidInput,
                $"Invalid controller input: {check}", input.Model, input.Policy ?? ControllerPolicy.Default, _previous);
            return _previous;
        }

        _previous = Compute(input);
        return _previous;
    }

    private TrainControllerOutput Compute(TrainControllerInput input)
    {
        var m = input.Model;
        var d = input.Driver;
        var car = input.Vehicle;
        var cfg = input.Policy;
        var dt = input.DeltaTimeSeconds;
        var speed = m.ActualSpeedMetersPerSecond;
        var manualMode = d.Mode == OperatingMode.Manual;
        var isStopped = speed <= cfg.StoppedSpeedThresholdMetersPerSecond;
        var sb = car.ServiceBrakeDecelerationMetersPerSecondSquared;
        var notes = new List<string>();
        var travelled = Math.Max(0.0, speed) * dt;

        // ---- beacon / station target ----
        var beaconUsed = m.Beacon.IsValid && m.Beacon.IsNewlyReceived;
        if (beaconUsed)
        {
            _station = new StationTarget(m.Beacon.NextStationName, m.Beacon.DistanceToStationMeters, m.Beacon.PlatformSide);
        }
        else if (_station is not null)
        {
            _station.Distance -= travelled;
        }

        // ---- authority estimate: trust received values only while the track signal is valid ----
        var authorityFresh = m.TrackSignalValid && (!_authorityKnown || m.AuthorityUpdateReceived);
        if (authorityFresh)
        {
            _authorityLeft = Math.Max(0.0, m.RemainingAuthorityMeters);
            _authorityKnown = true;
        }
        else
        {
            // No trusted value yet -> no authority; otherwise count the trusted value down.
            _authorityLeft = _authorityKnown ? Math.Max(0.0, _authorityLeft - travelled) : 0.0;
            if (!m.TrackSignalValid && m.AuthorityUpdateReceived)
            {
                notes.Add("Authority update ignored: track signal invalid.");
            }
        }

        // ---- station departed / passed ----
        var stationEvent = StationEvent.None;
        if (_station is not null)
        {
            var label = _station.Label;
            if (_station.Served && !isStopped)
            {
                _station = null;
                stationEvent = StationEvent.Departed;
            }
            else if (_station.Distance < -cfg.StationDistanceThresholdMeters)
            {
                _station = null;
                stationEvent = StationEvent.PassedWithoutStopping;
                notes.Add($"Passed {label} without stopping.");
            }
        }

        // ---- failures (previous tick's commands judged against this tick's speed) ----
        var watch = _watch.Update(speed, m.TrackSignalValid, isStopped, dt, d.ResetFailuresRequested, car.TotalRatedPowerWatts, cfg);
        var failed = _watch.Latched;
        var pickupFailed = (failed & ControllerFailure.SignalPickup) != 0;
        var brakesFailed = (failed & ControllerFailure.Brake) != 0;
        var engineFailed = (failed & ControllerFailure.Engine) != 0;
        if (watch.Rejected.Length > 0)
        {
            notes.Add($"Failure reset rejected: {watch.Rejected}.");
        }

        // ---- track signal / commanded speed ----
        var signalLost = !m.TrackSignalValid;
        var holdForSignal = signalLost || pickupFailed;
        var trustedCommand = holdForSignal ? 0.0 : m.CommandedSpeedMetersPerSecond;
        var speedLimit = Math.Min(trustedCommand, car.MaxSpeedMetersPerSecond);
        if (signalLost)
        {
            notes.Add("Track signal lost: stopping the train with the service brake.");
        }

        if (pickupFailed)
        {
            notes.Add(m.TrackSignalValid
                ? "SIGNAL PICKUP FAILURE: signal valid again; press Reset failures to resume."
                : "SIGNAL PICKUP FAILURE: train held with the service brake.");
        }

        if (brakesFailed)
        {
            notes.Add("BRAKE FAILURE: emergency brake applied. Reset failures once stopped.");
        }

        if (engineFailed)
        {
            notes.Add("ENGINE FAILURE: power 0, service brake applied. Reset failures once stopped.");
        }

        // ---- authority protection (independent safety backstop) ----
        bool authorityService, authorityEmergency;
        double toAuthorityBrakePoint;
        if (isStopped)
        {
            authorityService = _authorityLeft <= cfg.AuthorityBrakingMarginMeters;
            authorityEmergency = false;
            toAuthorityBrakePoint = _authorityLeft - cfg.AuthorityBrakingMarginMeters;
        }
        else
        {
            var needService = Square(speed) / (2.0 * sb) + cfg.AuthorityBrakingMarginMeters + speed * dt;
            var needEmergency = Square(speed) / (2.0 * car.EmergencyBrakeDecelerationMetersPerSecondSquared);
            authorityService = _authorityLeft <= needService;
            authorityEmergency = _authorityLeft < needEmergency;
            toAuthorityBrakePoint = _authorityLeft - needService;
        }

        // ---- emergency brake ----
        // Driver / passenger: latched, E-brake reset only when stopped and no passenger request.
        // Authority: applied while it cannot stop in time, released once stopped.
        const EmergencyBrakeCause pressCauses = EmergencyBrakeCause.Driver | EmergencyBrakeCause.Passenger;
        var blockedBecause = m.PassengerEmergencyBrakeRequested ? "passenger emergency brake request is still active"
            : !isStopped ? "train is still moving"
            : string.Empty;

        var resetRefused = false;
        if (d.EmergencyBrakeResetRequested && (_latched & pressCauses) != 0)
        {
            if (blockedBecause.Length > 0)
            {
                resetRefused = true;
                notes.Add($"Emergency brake reset rejected: {blockedBecause}.");
            }
            else
            {
                _latched &= ~pressCauses;
            }
        }

        if (d.EmergencyBrakeRequested) _latched |= EmergencyBrakeCause.Driver;
        if (m.PassengerEmergencyBrakeRequested) _latched |= EmergencyBrakeCause.Passenger;
        if (authorityEmergency) _latched |= EmergencyBrakeCause.AuthorityViolation;
        else if (isStopped) _latched &= ~EmergencyBrakeCause.AuthorityViolation;

        var ebCauses = _latched | (brakesFailed ? EmergencyBrakeCause.BrakeFailure : EmergencyBrakeCause.None);
        var emergencyOn = ebCauses != EmergencyBrakeCause.None;

        // ---- station braking: automatic in both modes ----
        var stationStop = Square(Math.Max(0.0, speed)) / (2.0 * sb) + cfg.StationBrakingMarginMeters;
        var hasStation = _station is not null && double.IsFinite(_station.Distance);
        var atStation = hasStation && Math.Abs(_station!.Distance) <= cfg.StationDistanceThresholdMeters && isStopped;

        double? toBrakePoint = null;
        var stationBrake = false;
        if (hasStation && !_station!.Served)
        {
            toBrakePoint = _station.Distance - speed * dt - stationStop;
            stationBrake = !atStation && toBrakePoint.Value <= 0.0;
        }

        // ---- arrival / dwell (both modes) ----
        var dwellNow = false;
        var openLeftForDwell = false;
        var openRightForDwell = false;
        if (atStation && !_station!.Served)
        {
            if (!_station.Dwelling)
            {
                stationEvent = StationEvent.Arrived;
            }

            _station.Dwelling = true;
            _station.DwellSeconds += dt;
            if (_station.DwellSeconds >= cfg.StationDwellTimeSeconds)
            {
                _station.MarkServed();
            }
            else
            {
                dwellNow = true;
                openLeftForDwell = _station.Side is PlatformSide.Left or PlatformSide.Both;
                openRightForDwell = _station.Side is PlatformSide.Right or PlatformSide.Both;
                if (_station.Side == PlatformSide.None)
                {
                    notes.Add($"No platform side known for {_station.Label}: doors stay closed.");
                }
            }
        }

        // ---- announcement (event-like) ----
        var announce = string.Empty;
        if (stationEvent == StationEvent.Arrived && _station is not null && _station.Name.Length > 0)
        {
            announce = $"Arrived at {_station.Name}.";
        }
        else if (beaconUsed && _station is not null && _station.Name.Length > 0)
        {
            var doorSide = _station.Side switch
            {
                PlatformSide.Left => " Doors will open on the left.",
                PlatformSide.Right => " Doors will open on the right.",
                PlatformSide.Both => " Doors will open on both sides.",
                _ => string.Empty,
            };
            announce = $"Next station: {_station.Name}.{doorSide}";
        }

        var driverText = d.AnnouncementRequest.Trim();
        if (driverText.Length > 0)
        {
            announce = driverText;
        }

        // ---- doors: dwell side or driver request while stopped (both modes) ----
        var wantsOpen = d.LeftDoorsOpenRequested || d.RightDoorsOpenRequested;
        bool leftCmd, rightCmd, refused;
        if (!isStopped)
        {
            leftCmd = rightCmd = false;
            refused = wantsOpen;
        }
        else
        {
            leftCmd = openLeftForDwell || d.LeftDoorsOpenRequested;
            rightCmd = openRightForDwell || d.RightDoorsOpenRequested;
            refused = false;
        }

        var doorsAreOpen = m.LeftDoorsOpen || m.RightDoorsOpen;
        var doorsCommanded = leftCmd || rightCmd;
        var interlock = doorsAreOpen || doorsCommanded || refused;
        if (refused)
        {
            notes.Add("Door open request refused: train is moving.");
        }
        else if (doorsAreOpen)
        {
            notes.Add("Doors open: traction inhibited.");
        }

        // ---- overspeed ----
        var tooFast = speed > speedLimit;
        if (tooFast && !holdForSignal)
        {
            notes.Add("Overspeed: service brake applied.");
        }

        // ---- setpoint: minimum of all constraints, earliest wins ties ----
        var target = double.PositiveInfinity;
        var limitedBy = TargetSpeedConstraint.None;
        void Cap(double value, TargetSpeedConstraint why)
        {
            var v = Math.Max(0.0, value);
            if (v < target)
            {
                target = v;
                limitedBy = why;
            }
        }

        Cap(trustedCommand, signalLost ? TargetSpeedConstraint.TrackSignalLoss
            : pickupFailed ? TargetSpeedConstraint.Failure
            : TargetSpeedConstraint.CommandedSpeed);
        Cap(car.MaxSpeedMetersPerSecond, TargetSpeedConstraint.VehicleMaximum);
        Cap(StopFrom(_authorityLeft - cfg.AuthorityBrakingMarginMeters, sb), TargetSpeedConstraint.RemainingAuthority);
        if (manualMode && d.RequestedSpeedMetersPerSecond.HasValue)
        {
            Cap(d.RequestedSpeedMetersPerSecond.Value, TargetSpeedConstraint.DriverRequest);
        }

        if (stationBrake || dwellNow)
        {
            Cap(0.0, TargetSpeedConstraint.StationStop);
        }
        else if (hasStation && !_station!.Served)
        {
            Cap(StopFrom(_station.Distance - cfg.StationBrakingMarginMeters, sb), TargetSpeedConstraint.StationStop);
        }

        if (brakesFailed || engineFailed) Cap(0.0, TargetSpeedConstraint.Failure);
        if (emergencyOn) Cap(0.0, TargetSpeedConstraint.EmergencyBrake);
        if (d.ServiceBrakeRequested) Cap(0.0, TargetSpeedConstraint.DriverServiceBrake);

        if (double.IsPositiveInfinity(target))
        {
            target = 0.0;
            limitedBy = TargetSpeedConstraint.None;
        }

        var hold = isStopped && (target <= cfg.StoppedSpeedThresholdMetersPerSecond || doorsAreOpen || doorsCommanded);

        if (authorityService || authorityEmergency)
        {
            notes.Add(authorityEmergency
                ? "Authority overrun risk: emergency brake applied."
                : "Authority protection: service brake applied.");
        }

        if (emergencyOn)
        {
            notes.Add(EmergencyNote(ebCauses, m.PassengerEmergencyBrakeRequested));
        }

        // ---- brakes, then traction ----
        var serviceOn = d.ServiceBrakeRequested || authorityService || tooFast || stationBrake || hold
            || holdForSignal || engineFailed;

        double watts = 0.0;
        if (!serviceOn && !emergencyOn && !interlock && failed == ControllerFailure.None)
        {
            watts = PiPower(target - speed, input.Engineer, dt, car.TotalRatedPowerWatts);
        }
        else
        {
            _integral = 0.0;
        }

        _watch.Remember(speed, serviceOn, emergencyOn, watts, target);

        var powerState = emergencyOn ? TractionState.EmergencyBrake
            : interlock ? TractionState.DoorInterlock
            : hold ? TractionState.HoldingStopped
            : serviceOn ? TractionState.ServiceBrake
            : watts > 0.0 ? TractionState.Powering
            : TractionState.AtOrAboveTarget;

        return new TrainControllerOutput
        {
            TrainId = TrainId,
            TickId = input.TickId,
            Commands = new TrainModelCommand
            {
                PowerCommandWatts = watts,
                ServiceBrakeCommand = serviceOn,
                EmergencyBrakeCommand = emergencyOn,
                LeftDoorsOpenCommand = leftCmd,
                RightDoorsOpenCommand = rightCmd,
                ExteriorLightsCommand = d.ExteriorLightsRequested,
                CabinTemperatureSetpointCelsius = d.CabinTemperatureSetpointCelsius,
                StationAnnouncement = announce,
            },
            Display = new DriverDisplayState
            {
                SetpointSpeedMetersPerSecond = target,
                TargetLimitedBy = limitedBy,
                CommandedSpeedMetersPerSecond = m.CommandedSpeedMetersPerSecond,
                TrafficLight = m.TrafficLight,
                NextStationName = _station?.Name ?? string.Empty,
                DistanceToNextStationMeters = hasStation && _station is not null ? _station.Distance : null,
                PlatformSide = _station?.Side ?? PlatformSide.None,
                RemainingAuthorityMeters = _authorityLeft,
                AuthorityRecalibrated = authorityFresh,
                ServiceBrakeStoppingDistanceMeters = stationStop,
                DistanceToStationBrakePointMeters = toBrakePoint,
                DistanceToAuthorityBrakePointMeters = toAuthorityBrakePoint,
                StationBrakingActive = stationBrake,
                AtStation = atStation,
                StationEvent = stationEvent,
                TractionState = powerState,
                AuthorityProtectionActive = authorityService || authorityEmergency,
                OverspeedProtectionActive = tooFast,
                DoorInterlockActive = interlock,
                EmergencyBrakeCauses = ebCauses,
                EmergencyBrakeLatched = emergencyOn,
                EmergencyBrakeResetBlockedReason = (_latched & pressCauses) != 0 ? blockedBecause : string.Empty,
                EmergencyBrakeResetRejected = resetRefused,
                Failures = failed,
                FailureResetRejected = watch.Rejected.Length > 0,
                FailureResetRejectedReason = watch.Rejected,
                FailureResetBlockedReason = watch.Blocked,
                BeaconRecalibrated = beaconUsed,
                TrackSignalLost = signalLost,
                Alerts = notes,
            },
        };
    }

    private static string EmergencyNote(EmergencyBrakeCause causes, bool passengerActive)
    {
        var why = new List<string>();
        if ((causes & EmergencyBrakeCause.Passenger) != 0) why.Add(passengerActive ? "passenger request (active)" : "passenger request (cleared)");
        if ((causes & EmergencyBrakeCause.Driver) != 0) why.Add("driver");
        if ((causes & EmergencyBrakeCause.AuthorityViolation) != 0) why.Add("authority");
        if ((causes & EmergencyBrakeCause.BrakeFailure) != 0) why.Add("brake failure");
        return $"Emergency brake applied ({string.Join(", ", why)}).";
    }

    private double PiPower(double error, EngineerSettings gains, double dt, double ratedWatts)
    {
        var nextIntegral = _integral + error * dt;
        var raw = gains.Kp * error + gains.Ki * nextIntegral;
        var pushingPastMax = raw > ratedWatts && error > 0.0;
        var pushingPastZero = raw < 0.0 && error < 0.0;
        if (!pushingPastMax && !pushingPastZero)
        {
            _integral = nextIntegral;
        }

        return Math.Clamp(raw, 0.0, ratedWatts);
    }

    /// <summary>Highest speed that still stops within <paramref name="meters"/> at deceleration <paramref name="decel"/>.</summary>
    private static double StopFrom(double meters, double decel) => Math.Sqrt(2.0 * decel * Math.Max(0.0, meters));

    private static double Square(double x) => x * x;

    /// <summary>Next-station target learned from a beacon.</summary>
    private sealed class StationTarget
    {
        public StationTarget(string name, double distance, PlatformSide side)
        {
            Name = name ?? string.Empty;
            Distance = distance;
            Side = side;
        }

        public string Name { get; }

        public string Label => Name.Length > 0 ? Name : "next station";

        public double Distance { get; set; }

        public PlatformSide Side { get; }

        public bool Served { get; private set; }

        public bool Dwelling { get; set; }

        public double DwellSeconds { get; set; }

        public void MarkServed()
        {
            Served = true;
            Dwelling = false;
            DwellSeconds = 0.0;
        }
    }

    /// <summary>
    /// Failure latches and detection windows for this train (independent of TrainController.Core).
    /// Signal pickup: signal invalid for SignalPickupFailureLatchSeconds. Brake: service brake (no
    /// emergency brake) above the minimum speed for BrakeFailureDetectionSeconds with average
    /// deceleration below the minimum. Engine: no brake, speed error and power above their
    /// minimums for EngineFailureDetectionSeconds with average acceleration at or below the maximum.
    /// Speed changes are judged against the commands remembered from the previous tick.
    /// </summary>
    private sealed class FailureWatch
    {
        private const double Eps = 1e-9;

        private double _invalidFor;
        private bool _brakeTiming;
        private double _brakeFrom;
        private double _brakeFor;
        private bool _engineTiming;
        private double _engineFrom;
        private double _engineFor;

        private bool _known;
        private double _lastSpeed;
        private bool _lastService;
        private bool _lastEmergency;
        private double _lastWatts;
        private double _lastTarget;

        public ControllerFailure Latched { get; private set; }

        public (string Rejected, string Blocked) Update(
            double speed, bool signalValid, bool stopped, double dt, bool resetPressed, double ratedWatts, ControllerPolicy cfg)
        {
            var refusals = new List<string>();
            if (resetPressed && Latched != ControllerFailure.None)
            {
                var keep = Latched;
                if ((Latched & ControllerFailure.SignalPickup) != 0)
                {
                    if (signalValid) keep &= ~ControllerFailure.SignalPickup;
                    else refusals.Add("signal pickup failure: track signal still invalid");
                }

                if ((Latched & ControllerFailure.Brake) != 0)
                {
                    if (stopped) { keep &= ~ControllerFailure.Brake; _brakeTiming = false; }
                    else refusals.Add("brake failure: train still moving");
                }

                if ((Latched & ControllerFailure.Engine) != 0)
                {
                    if (stopped) { keep &= ~ControllerFailure.Engine; _engineTiming = false; }
                    else refusals.Add("engine failure: train still moving");
                }

                Latched = keep;
            }

            // signal pickup
            _invalidFor = signalValid ? 0.0 : _invalidFor + dt;
            if (!signalValid && _invalidFor + Eps >= cfg.SignalPickupFailureLatchSeconds)
            {
                Latched |= ControllerFailure.SignalPickup;
            }

            // brake
            if ((Latched & ControllerFailure.Brake) == 0
                && _known && _lastService && !_lastEmergency && _lastSpeed > cfg.BrakeFailureMinimumSpeedMetersPerSecond)
            {
                if (!_brakeTiming)
                {
                    _brakeTiming = true;
                    _brakeFrom = _lastSpeed;
                    _brakeFor = 0.0;
                }

                _brakeFor += dt;
                if (_brakeFor + Eps >= cfg.BrakeFailureDetectionSeconds)
                {
                    if ((_brakeFrom - speed) / _brakeFor < cfg.BrakeFailureMinimumDecelerationMetersPerSecondSquared)
                    {
                        Latched |= ControllerFailure.Brake;
                    }

                    _brakeTiming = false;
                }
            }
            else
            {
                _brakeTiming = false;
            }

            // engine
            if ((Latched & ControllerFailure.Engine) == 0
                && _known && !_lastService && !_lastEmergency
                && _lastTarget - _lastSpeed > cfg.EngineFailureMinimumSpeedErrorMetersPerSecond
                && _lastWatts >= cfg.EngineFailureMinimumPowerFraction * ratedWatts)
            {
                if (!_engineTiming)
                {
                    _engineTiming = true;
                    _engineFrom = _lastSpeed;
                    _engineFor = 0.0;
                }

                _engineFor += dt;
                if (_engineFor + Eps >= cfg.EngineFailureDetectionSeconds)
                {
                    if ((speed - _engineFrom) / _engineFor <= cfg.EngineFailureMaximumAccelerationMetersPerSecondSquared)
                    {
                        Latched |= ControllerFailure.Engine;
                    }

                    _engineTiming = false;
                }
            }
            else
            {
                _engineTiming = false;
            }

            var blockers = new List<string>();
            if ((Latched & ControllerFailure.SignalPickup) != 0 && !signalValid) blockers.Add("track signal still invalid");
            if ((Latched & (ControllerFailure.Brake | ControllerFailure.Engine)) != 0 && !stopped) blockers.Add("train still moving");
            return (string.Join("; ", refusals), string.Join("; ", blockers));
        }

        public void Remember(double speed, bool service, bool emergency, double watts, double target)
        {
            _known = true;
            _lastSpeed = speed;
            _lastService = service;
            _lastEmergency = emergency;
            _lastWatts = watts;
            _lastTarget = target;
        }

        public void NoCommand()
        {
            _known = false;
            _brakeTiming = false;
            _engineTiming = false;
        }

        public void Clear()
        {
            Latched = ControllerFailure.None;
            _invalidFor = 0.0;
            NoCommand();
        }
    }
}
