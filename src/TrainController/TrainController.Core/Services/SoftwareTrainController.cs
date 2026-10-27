using TrainControl.Contracts.Enums;
using TrainController.Abstractions.Configuration;
using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;
using TrainController.Abstractions.Validation;
using TrainController.Core.Control;
using TrainController.Core.Safety;

namespace TrainController.Core.Services;

/// <summary>
/// Software Train Controller for ONE train. Each call to <see cref="Step"/> executes exactly
/// one control step for one tick. All runtime state (PI integral, emergency latch, failure
/// latches and detection windows, station tracking, dwell, previous output) lives in this
/// instance and is never shared.
/// </summary>
/// <remarks>
/// <para>Per tick, in order:</para>
/// <list type="number">
/// <item>Validate input (invalid → fail-safe output).</item>
/// <item>Station tracking: newly received valid beacon recalibrates; otherwise dead-reckon v·dt
///       (signed). The target is cleared after departing a served station or passing it.
///       Remaining authority: recalibrated only from authority updates received while the track
///       signal is valid; otherwise dead-reckoned v·dt from the last trusted value.</item>
/// <item>Failure detection (<see cref="FailureMonitor"/>): Signal Pickup, Brake, Engine — inferred,
///       latched, cleared only by "Reset failures" when each reset condition holds.</item>
/// <item>Setpoint speed = minimum of: commanded speed, vehicle maximum, driver request (Manual,
///       only when set), the remaining-authority braking curve √(2·a·(authority − margin)), and
///       the station braking curve / station stop (BOTH modes). 0 while the track signal is
///       invalid or a signal-pickup failure is latched, while a brake or engine failure is
///       latched, while the emergency brake is applied, or while the driver holds the service brake.</item>
/// <item>Authority protection (service brake / emergency brake), always automatic.</item>
/// <item>Emergency brake: driver press / passenger request (E-brake reset), authority violation
///       (released once stopped), brake failure (Reset failures).</item>
/// <item>Station braking: automatic in both modes once the service-brake stopping distance
///       reaches the distance to the station.</item>
/// <item>Arrival by thresholds (never exact zero); dwell opens the platform side (both modes).</item>
/// <item>Doors: dwell side or driver request while stopped; door/motion interlock; overspeed
///       protection; holding brake when stopped and not moving off.</item>
/// <item>PI traction power only when no brake is applied, doors are closed and no failure is latched.</item>
/// </list>
/// <para>
/// Manual mode differs from Automatic ONLY in that the driver may lower the speed with a requested
/// speed. The traffic light is passed through for display only (no control logic yet).
/// All units SI. This class must stay free of UI, transport and Hardware-controller code.
/// The Hardware controller on the Raspberry Pi implements the same requirements independently.
/// </para>
/// </remarks>
public sealed class SoftwareTrainController
{
    private readonly SpeedController _speed = new SpeedController();
    private readonly BrakeController _brakes = new BrakeController();
    private readonly StationTracker _station = new StationTracker();
    private readonly AuthorityTracker _authority = new AuthorityTracker();
    private readonly FailureMonitor _failures = new FailureMonitor();
    private TrainControllerOutput? _lastOutput;

    public SoftwareTrainController(string trainId)
    {
        if (string.IsNullOrWhiteSpace(trainId))
        {
            throw new ArgumentException("TrainId is required.", nameof(trainId));
        }

        TrainId = trainId;
    }

    public string TrainId { get; }

    /// <summary>PI integral (m) — exposed for diagnostics and tests.</summary>
    public double IntegralMeters => _speed.IntegralMeters;

    public EmergencyBrakeCause LatchedEmergencyCauses => _brakes.LatchedCauses;

    public ControllerFailure LatchedFailures => _failures.Latched;

    public TrainControllerOutput? LastOutput => _lastOutput;

    /// <exception cref="ArgumentException">The input is for a different train.</exception>
    public TrainControllerOutput Step(TrainControllerInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!string.Equals(input.TrainId, TrainId, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Controller for '{TrainId}' received input for '{input.TrainId}'.", nameof(input));
        }

        var validation = TrainControllerInputValidator.Validate(input);
        if (!validation.IsValid)
        {
            _speed.ResetIntegral();
            _failures.ForgetCommands();
            _lastOutput = FailSafeOutput.Create(
                TrainId,
                input.TickId,
                EmergencyBrakeCause.InvalidInput,
                $"Invalid controller input: {validation}",
                input.Model,
                input.Policy ?? ControllerPolicy.Default,
                _lastOutput);
            return _lastOutput;
        }

        _lastOutput = Evaluate(input);
        return _lastOutput;
    }

    /// <summary>Clears ALL runtime state (Test Simulation Reset). Engineer settings live outside.</summary>
    public void Reset()
    {
        _speed.ResetIntegral();
        _brakes.Reset();
        _station.Reset();
        _authority.Reset();
        _failures.Reset();
        _lastOutput = null;
    }

    private TrainControllerOutput Evaluate(TrainControllerInput input)
    {
        var model = input.Model;
        var driver = input.Driver;
        var vehicle = input.Vehicle;
        var policy = input.Policy;
        var dt = input.DeltaTimeSeconds;
        var v = model.ActualSpeedMetersPerSecond;
        var manual = driver.Mode == OperatingMode.Manual;
        var stopped = v <= policy.StoppedSpeedThresholdMetersPerSecond;
        var alerts = new List<string>();

        // 1. Station-relative distance and remaining-authority tracking (recalibrate on new
        //    data, otherwise dead-reckon v·dt).
        var recalibrated = _station.Update(model.Beacon, v, dt);
        var authorityRecalibrated = _authority.Update(model, dt, out var authorityUpdateIgnored);
        var remainingAuthority = _authority.RemainingMeters;
        if (authorityUpdateIgnored)
        {
            alerts.Add("Authority update ignored: track signal invalid.");
        }

        var stationEvent = StationEvent.None;
        if (_station.HasEstimate)
        {
            var name = StationLabel();
            if (_station.StationServiced && !stopped)
            {
                _station.Clear();
                stationEvent = StationEvent.Departed;
            }
            else if (_station.DistanceToNextStationMeters < -policy.StationDistanceThresholdMeters)
            {
                _station.Clear();
                stationEvent = StationEvent.PassedWithoutStopping;
                alerts.Add($"Passed {name} without stopping.");
            }
        }

        // 2. Failure detection (judges the PREVIOUS tick's commands against this tick's speed).
        var failureCheck = _failures.Evaluate(
            v, model.TrackSignalValid, stopped, dt, driver.ResetFailuresRequested, vehicle.TotalRatedPowerWatts, policy);
        var failures = failureCheck.Latched;
        var signalPickupFailure = failures.HasFlag(ControllerFailure.SignalPickup);
        var brakeFailure = failures.HasFlag(ControllerFailure.Brake);
        var engineFailure = failures.HasFlag(ControllerFailure.Engine);
        if (failureCheck.ResetRejected)
        {
            alerts.Add($"Failure reset rejected: {failureCheck.ResetRejectedReason}.");
        }

        // 3. Track signal. An invalid signal stops the train with the service brake IMMEDIATELY;
        //    the latched Signal Pickup Failure keeps it stopped until reset. Commanded speed is
        //    not trusted while the signal is invalid.
        var signalLost = !model.TrackSignalValid;
        var signalRestricted = signalLost || signalPickupFailure;
        var commanded = signalRestricted ? 0.0 : model.CommandedSpeedMetersPerSecond;
        var limit = Math.Min(commanded, vehicle.MaxSpeedMetersPerSecond);
        if (signalLost)
        {
            alerts.Add("Track signal lost: stopping the train with the service brake.");
        }

        if (signalPickupFailure)
        {
            alerts.Add(model.TrackSignalValid
                ? "SIGNAL PICKUP FAILURE: signal valid again; press Reset failures to resume."
                : "SIGNAL PICKUP FAILURE: train held with the service brake.");
        }

        if (brakeFailure)
        {
            alerts.Add("BRAKE FAILURE: emergency brake applied. Reset failures once stopped.");
        }

        if (engineFailure)
        {
            alerts.Add("ENGINE FAILURE: power 0, service brake applied. Reset failures once stopped.");
        }

        // 4. Authority protection (always automatic).
        var authority = AuthorityProtection.Evaluate(v, remainingAuthority, dt, vehicle, policy);
        var distanceToAuthorityBrakePoint = AuthorityProtection.DistanceToBrakePoint(v, remainingAuthority, dt, vehicle, policy);

        // 5. Emergency brake: driver / passenger latch (E-brake reset), authority (released when
        //    stopped), brake failure (Reset failures).
        var emergency = _brakes.Evaluate(
            driver.EmergencyBrakeRequested,
            model.PassengerEmergencyBrakeRequested,
            authority.EmergencyBrake,
            stopped,
            driver.EmergencyBrakeResetRequested);

        if (emergency.ResetRejected)
        {
            alerts.Add($"Emergency brake reset rejected: {emergency.ResetRejectedReason}.");
        }

        var emergencyCauses = emergency.LatchedCauses | (brakeFailure ? EmergencyBrakeCause.BrakeFailure : EmergencyBrakeCause.None);
        var emergencyBrake = emergencyCauses != EmergencyBrakeCause.None;

        // 6. Station braking — automatic in BOTH modes.
        var stationStoppingDistance =
            StoppingDistance.Compute(v, vehicle.ServiceBrakeDecelerationMetersPerSecondSquared)
            + policy.StationBrakingMarginMeters;

        var atStation = _station.HasValidDistance
            && Math.Abs(_station.DistanceToNextStationMeters) <= policy.StationDistanceThresholdMeters
            && stopped;

        double? distanceToBrakePoint = null;
        var stationBrakingActive = false;
        if (_station.HasValidDistance && !_station.StationServiced)
        {
            // Latest braking point, looking one control period ahead (discretization, not a margin).
            distanceToBrakePoint = _station.DistanceToNextStationMeters - v * dt - stationStoppingDistance;
            stationBrakingActive = !atStation && distanceToBrakePoint <= 0.0;
        }

        // 7. Arrival and dwell (both modes).
        var dwellOpenLeft = false;
        var dwellOpenRight = false;
        var dwelling = false;
        if (atStation && !_station.StationServiced)
        {
            if (!_station.IsDwelling)
            {
                stationEvent = StationEvent.Arrived;
            }

            _station.AdvanceDwell(dt);
            if (_station.DwellElapsedSeconds >= policy.StationDwellTimeSeconds)
            {
                _station.CompleteService();
            }
            else
            {
                dwelling = true;
                dwellOpenLeft = _station.PlatformSide is PlatformSide.Left or PlatformSide.Both;
                dwellOpenRight = _station.PlatformSide is PlatformSide.Right or PlatformSide.Both;
                if (_station.PlatformSide == PlatformSide.None)
                {
                    alerts.Add($"No platform side known for {StationLabel()}: doors stay closed.");
                }
            }
        }

        // Announcements are events: once when a new valid beacon names the next station, once
        // on arrival. A driver announcement takes precedence on the tick it is sent.
        // (If both happen on the same tick — beacon read while already stopped at the station —
        // the arrival is announced.)
        var announcement = string.Empty;
        if (stationEvent == StationEvent.Arrived && _station.NextStationName.Length > 0)
        {
            announcement = $"Arrived at {_station.NextStationName}.";
        }
        else if (recalibrated && _station.NextStationName.Length > 0)
        {
            announcement = $"Next station: {_station.NextStationName}.{DoorSideSuffix(_station.PlatformSide)}";
        }

        var driverAnnouncement = driver.AnnouncementRequest.Trim();
        if (driverAnnouncement.Length > 0)
        {
            announcement = driverAnnouncement;
        }

        // 8. Doors.
        var doors = DoorController.Decide(stopped, dwellOpenLeft, dwellOpenRight, driver);
        var doorsPhysicallyOpen = model.LeftDoorsOpen || model.RightDoorsOpen;
        var doorsCommandedOpen = doors.LeftOpen || doors.RightOpen;
        var doorInterlock = doorsPhysicallyOpen || doorsCommandedOpen || doors.RequestRefused;
        if (doors.RequestRefused)
        {
            alerts.Add("Door open request refused: train is moving.");
        }
        else if (doorsPhysicallyOpen)
        {
            alerts.Add("Doors open: traction inhibited.");
        }

        // 9. Overspeed.
        var overspeed = OverspeedProtection.IsOverspeed(v, limit);
        if (overspeed && !signalRestricted)
        {
            alerts.Add("Overspeed: service brake applied.");
        }

        // Setpoint speed. The remaining-authority braking curve is a PREVENTIVE limit (the PI never
        // aims faster than the train can stop within its authority); AuthorityProtection (step 4)
        // is the INDEPENDENT safety backstop that brakes regardless of the setpoint.
        var serviceDecel = vehicle.ServiceBrakeDecelerationMetersPerSecondSquared;
        var selector = new TargetSpeedSelector()
            .Limit(commanded,
                signalLost ? TargetSpeedConstraint.TrackSignalLoss
                : signalPickupFailure ? TargetSpeedConstraint.Failure
                : TargetSpeedConstraint.CommandedSpeed)
            .Limit(vehicle.MaxSpeedMetersPerSecond, TargetSpeedConstraint.VehicleMaximum)
            .Limit(
                TargetSpeedSelector.BrakingCurveSpeed(remainingAuthority - policy.AuthorityBrakingMarginMeters, serviceDecel),
                TargetSpeedConstraint.RemainingAuthority);

        if (manual && driver.RequestedSpeedMetersPerSecond is double requested)
        {
            // Manual: the request is only ONE constraint; it can never raise the setpoint.
            selector.Limit(requested, TargetSpeedConstraint.DriverRequest);
        }

        if (stationBrakingActive || dwelling)
        {
            selector.Limit(0.0, TargetSpeedConstraint.StationStop);
        }
        else if (_station.HasValidDistance && !_station.StationServiced)
        {
            // Station braking curve: only with a valid next-station distance.
            selector.Limit(
                TargetSpeedSelector.BrakingCurveSpeed(_station.DistanceToNextStationMeters - policy.StationBrakingMarginMeters, serviceDecel),
                TargetSpeedConstraint.StationStop);
        }

        if (brakeFailure || engineFailure)
        {
            selector.Limit(0.0, TargetSpeedConstraint.Failure);
        }

        if (emergencyBrake)
        {
            selector.Limit(0.0, TargetSpeedConstraint.EmergencyBrake);
        }

        if (driver.ServiceBrakeRequested)
        {
            // The driver is braking: the controller must not aim for any speed while the brake is
            // held. The request setting itself is untouched and applies again on release.
            selector.Limit(0.0, TargetSpeedConstraint.DriverServiceBrake);
        }

        var target = selector.Result;
        var setpoint = target.MetersPerSecond;
        var holdingBrake = stopped
            && (setpoint <= policy.StoppedSpeedThresholdMetersPerSecond || doorsPhysicallyOpen || doorsCommandedOpen);

        if (authority.Active)
        {
            alerts.Add(authority.EmergencyBrake
                ? "Authority overrun risk: emergency brake applied."
                : "Authority protection: service brake applied.");
        }

        if (emergencyBrake)
        {
            alerts.Add(DescribeEmergency(emergencyCauses, model.PassengerEmergencyBrakeRequested));
        }

        // 10. Brakes and traction.
        var serviceBrake = driver.ServiceBrakeRequested
            || authority.ServiceBrake
            || overspeed
            || stationBrakingActive
            || holdingBrake
            || signalRestricted
            || engineFailure;

        double power;
        if (!serviceBrake && !emergencyBrake && !doorInterlock && failures == ControllerFailure.None)
        {
            power = _speed.ComputePower(setpoint, v, input.Engineer, dt, vehicle.TotalRatedPowerWatts);
        }
        else
        {
            power = 0.0;
            _speed.ResetIntegral();
        }

        _failures.RecordCommands(v, serviceBrake, emergencyBrake, power, setpoint);

        var traction = emergencyBrake ? TractionState.EmergencyBrake
            : doorInterlock ? TractionState.DoorInterlock
            : holdingBrake ? TractionState.HoldingStopped
            : serviceBrake ? TractionState.ServiceBrake
            : power > 0.0 ? TractionState.Powering
            : TractionState.AtOrAboveTarget;

        return new TrainControllerOutput
        {
            TrainId = TrainId,
            TickId = input.TickId,
            Commands = new TrainModelCommand
            {
                PowerCommandWatts = power,
                ServiceBrakeCommand = serviceBrake,
                EmergencyBrakeCommand = emergencyBrake,
                LeftDoorsOpenCommand = doors.LeftOpen,
                RightDoorsOpenCommand = doors.RightOpen,
                ExteriorLightsCommand = driver.ExteriorLightsRequested,
                CabinTemperatureSetpointCelsius = driver.CabinTemperatureSetpointCelsius,
                StationAnnouncement = announcement,
            },
            Display = new DriverDisplayState
            {
                SetpointSpeedMetersPerSecond = setpoint,
                TargetLimitedBy = target.LimitedBy,
                CommandedSpeedMetersPerSecond = model.CommandedSpeedMetersPerSecond,
                TrafficLight = model.TrafficLight,
                NextStationName = _station.NextStationName,
                DistanceToNextStationMeters = _station.HasValidDistance ? _station.DistanceToNextStationMeters : null,
                PlatformSide = _station.PlatformSide,
                RemainingAuthorityMeters = remainingAuthority,
                AuthorityRecalibrated = authorityRecalibrated,
                ServiceBrakeStoppingDistanceMeters = stationStoppingDistance,
                DistanceToStationBrakePointMeters = distanceToBrakePoint,
                DistanceToAuthorityBrakePointMeters = distanceToAuthorityBrakePoint,
                StationBrakingActive = stationBrakingActive,
                AtStation = atStation,
                StationEvent = stationEvent,
                TractionState = traction,
                AuthorityProtectionActive = authority.Active,
                OverspeedProtectionActive = overspeed,
                DoorInterlockActive = doorInterlock,
                EmergencyBrakeCauses = emergencyCauses,
                EmergencyBrakeLatched = emergencyBrake,
                EmergencyBrakeResetRejected = emergency.ResetRejected,
                EmergencyBrakeResetBlockedReason = emergency.ResetBlockedReason,
                Failures = failures,
                FailureResetRejected = failureCheck.ResetRejected,
                FailureResetRejectedReason = failureCheck.ResetRejectedReason,
                FailureResetBlockedReason = failureCheck.ResetBlockedReason,
                BeaconRecalibrated = recalibrated,
                TrackSignalLost = signalLost,
                ControllerFaulted = false,
                FaultReason = string.Empty,
                Alerts = alerts,
            },
        };
    }

    private static string DoorSideSuffix(PlatformSide side) => side switch
    {
        PlatformSide.Left => " Doors will open on the left.",
        PlatformSide.Right => " Doors will open on the right.",
        PlatformSide.Both => " Doors will open on both sides.",
        _ => string.Empty,
    };

    private string StationLabel() =>
        _station.NextStationName.Length > 0 ? _station.NextStationName : "next station";

    private static string DescribeEmergency(EmergencyBrakeCause causes, bool passengerStillRequesting)
    {
        var parts = new List<string>();
        if (causes.HasFlag(EmergencyBrakeCause.Passenger))
        {
            parts.Add(passengerStillRequesting ? "passenger request (active)" : "passenger request (cleared)");
        }

        if (causes.HasFlag(EmergencyBrakeCause.Driver))
        {
            parts.Add("driver");
        }

        if (causes.HasFlag(EmergencyBrakeCause.AuthorityViolation))
        {
            parts.Add("authority");
        }

        if (causes.HasFlag(EmergencyBrakeCause.BrakeFailure))
        {
            parts.Add("brake failure");
        }

        return $"Emergency brake applied ({string.Join(", ", parts)}).";
    }
}
