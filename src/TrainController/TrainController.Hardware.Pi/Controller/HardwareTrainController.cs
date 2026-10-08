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

    public void Reset()
    {
        _integral = 0.0;
        _latched = EmergencyBrakeCause.None;
        _authorityKnown = false;
        _authorityLeft = 0.0;
        _station = null;
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
        var auto = d.Mode == OperatingMode.Automatic;
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

        // ---- limits ----
        var signalLost = !m.TrackSignalValid;
        var signalEmergency = signalLost && cfg.TrackSignalLossResponse == TrackSignalLossResponse.EmergencyBrake;
        var speedLimit = Math.Min(signalLost ? 0.0 : m.AuthorizedSpeedMetersPerSecond, car.MaxSpeedMetersPerSecond);
        if (signalLost)
        {
            notes.Add(signalEmergency
                ? "Track signal lost: emergency brake applied."
                : "Track signal lost: stopping the train with the service brake.");
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

        // ---- emergency latch ----
        var blockedBecause = m.PassengerEmergencyBrakeRequested ? "passenger emergency brake request is still active"
            : authorityEmergency ? "train still cannot stop within its remaining authority"
            : signalEmergency ? "track signal is still lost"
            : string.Empty;

        var resetRefused = false;
        if (d.EmergencyBrakeResetRequested && _latched != EmergencyBrakeCause.None)
        {
            if (blockedBecause.Length > 0)
            {
                resetRefused = true;
                notes.Add($"Emergency brake reset rejected: {blockedBecause}.");
            }
            else
            {
                _latched = EmergencyBrakeCause.None;
            }
        }

        if (d.EmergencyBrakeRequested) _latched |= EmergencyBrakeCause.Driver;
        if (m.PassengerEmergencyBrakeRequested) _latched |= EmergencyBrakeCause.Passenger;
        if (authorityEmergency) _latched |= EmergencyBrakeCause.AuthorityViolation;
        if (signalEmergency) _latched |= EmergencyBrakeCause.TrackSignalLoss;
        var emergencyOn = _latched != EmergencyBrakeCause.None;

        // ---- station guidance ----
        var stationStop = Square(Math.Max(0.0, speed)) / (2.0 * sb) + cfg.StationBrakingMarginMeters;
        var hasStation = _station is not null && double.IsFinite(_station.Distance);
        var atStation = hasStation && Math.Abs(_station!.Distance) <= cfg.StationDistanceThresholdMeters && isStopped;

        double? toBrakePoint = null;
        var brakingDue = false;
        if (hasStation && !_station!.Served)
        {
            toBrakePoint = _station.Distance - speed * dt - stationStop;
            brakingDue = !atStation && toBrakePoint.Value <= 0.0;
        }

        var advise = !auto && brakingDue;
        var autoBrake = auto && brakingDue;
        if (advise)
        {
            notes.Add($"Brake now for {_station!.Label}.");
        }

        // ---- arrival / dwell ----
        var dwellNow = false;
        var openLeftForDwell = false;
        var openRightForDwell = false;
        if (atStation && !_station!.Served)
        {
            if (!_station.Dwelling)
            {
                stationEvent = StationEvent.Arrived;
            }

            if (auto)
            {
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
            else
            {
                _station.MarkServed();
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

        // ---- doors ----
        var wantsOpen = d.LeftDoorsOpenRequested || d.RightDoorsOpenRequested;
        bool leftCmd, rightCmd, refused;
        if (!isStopped)
        {
            leftCmd = rightCmd = false;
            refused = wantsOpen;
        }
        else if (auto)
        {
            (leftCmd, rightCmd, refused) = (openLeftForDwell, openRightForDwell, false);
        }
        else
        {
            (leftCmd, rightCmd, refused) = (d.LeftDoorsOpenRequested, d.RightDoorsOpenRequested, false);
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
        if (tooFast)
        {
            notes.Add("Overspeed: service brake applied.");
        }

        // ---- effective target: minimum of all constraints, earliest wins ties ----
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

        Cap(signalLost ? 0.0 : m.AuthorizedSpeedMetersPerSecond, signalLost ? TargetSpeedConstraint.TrackSignalLoss : TargetSpeedConstraint.AuthorizedSpeed);
        Cap(car.MaxSpeedMetersPerSecond, TargetSpeedConstraint.VehicleMaximum);
        Cap(StopFrom(_authorityLeft - cfg.AuthorityBrakingMarginMeters, sb), TargetSpeedConstraint.RemainingAuthority);
        if (!auto)
        {
            Cap(d.RequestedSpeedMetersPerSecond, TargetSpeedConstraint.DriverRequest);
        }
        else if (autoBrake || dwellNow)
        {
            Cap(0.0, TargetSpeedConstraint.StationStop);
        }
        else if (hasStation && !_station!.Served)
        {
            Cap(StopFrom(_station.Distance - cfg.StationBrakingMarginMeters, sb), TargetSpeedConstraint.StationStop);
        }

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
            notes.Add("Emergency brake applied. Driver reset required.");
        }

        // ---- brakes, then traction ----
        var serviceOn = d.ServiceBrakeRequested || authorityService || tooFast || autoBrake || hold;

        double watts = 0.0;
        if (!serviceOn && !emergencyOn && !interlock)
        {
            watts = PiPower(target - speed, input.Engineer, dt, car.TotalRatedPowerWatts);
        }
        else
        {
            _integral = 0.0;
        }

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
                EffectiveTargetSpeedMetersPerSecond = target,
                TargetLimitedBy = limitedBy,
                NextStationName = _station?.Name ?? string.Empty,
                DistanceToNextStationMeters = hasStation && _station is not null ? _station.Distance : null,
                PlatformSide = _station?.Side ?? PlatformSide.None,
                RemainingAuthorityMeters = _authorityLeft,
                AuthorityRecalibrated = authorityFresh,
                ServiceBrakeStoppingDistanceMeters = stationStop,
                DistanceToStationBrakePointMeters = toBrakePoint,
                DistanceToAuthorityBrakePointMeters = toAuthorityBrakePoint,
                StationBrakingAdvised = advise,
                StationBrakingActive = autoBrake,
                AtStation = atStation,
                StationEvent = stationEvent,
                TractionState = powerState,
                AuthorityProtectionActive = authorityService || authorityEmergency,
                OverspeedProtectionActive = tooFast,
                DoorInterlockActive = interlock,
                EmergencyBrakeCauses = _latched,
                EmergencyBrakeLatched = emergencyOn,
                EmergencyBrakeResetBlockedReason = emergencyOn ? blockedBecause : string.Empty,
                EmergencyBrakeResetRejected = resetRefused,
                BeaconRecalibrated = beaconUsed,
                TrackSignalLost = signalLost,
                Alerts = notes,
            },
        };
    }

    /// <summary>PI law with conditional-integration anti-windup; output clamped to [0, rated power].</summary>
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
}
