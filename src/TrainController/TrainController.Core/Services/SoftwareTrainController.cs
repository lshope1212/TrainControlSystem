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
/// one control step for one tick. All runtime state (PI integral, emergency latch, station
/// tracking, dwell, previous output) lives in this instance and is never shared.
/// </summary>
/// <remarks>
/// <para>Per tick, in order:</para>
/// <list type="number">
/// <item>Validate input (invalid → fail-safe output).</item>
/// <item>Station tracking: newly received valid beacon recalibrates; otherwise dead-reckon v·dt
///       (signed). The target is cleared after departing a served station or passing it.
///       Remaining authority: recalibrated only from authority updates received while the track
///       signal is valid; otherwise dead-reckoned v·dt from the last trusted value.</item>
/// <item>Effective target = minimum of: authorized speed, vehicle maximum, driver request
///       (Manual only), the remaining-authority braking curve √(2·a·(authority − margin)),
///       and in Automatic the station braking curve / station stop. 0 if the track signal is
///       lost (plus an emergency brake if <see cref="ControllerPolicy.TrackSignalLossResponse"/>
///       says so), while the emergency brake is applied, or while the driver holds the
///       service brake.</item>
/// <item>Authority protection (service brake / emergency brake), always automatic.</item>
/// <item>Emergency-brake latch: driver press, passenger request, authority violation; driver reset.</item>
/// <item>Station guidance: service-brake stopping distance vs distance to station.
///       Manual → advisory only; Automatic → controller applies the service brake.</item>
/// <item>Arrival by thresholds (never exact zero); Automatic dwell opens the platform side.</item>
/// <item>Door/motion interlock; overspeed protection; holding brake when stopped and not moving off.</item>
/// <item>PI traction power only when no brake is applied and doors are closed.</item>
/// </list>
/// <para>
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
        var automatic = driver.Mode == OperatingMode.Automatic;
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

        // 2. Speed limit and target.
        var signalLost = !model.TrackSignalValid;
        var authorized = signalLost ? 0.0 : model.AuthorizedSpeedMetersPerSecond;
        var limit = Math.Min(authorized, vehicle.MaxSpeedMetersPerSecond);
        var signalLossEmergency = signalLost && policy.TrackSignalLossResponse == TrackSignalLossResponse.EmergencyBrake;
        if (signalLost)
        {
            alerts.Add(signalLossEmergency
                ? "Track signal lost: emergency brake applied."
                : "Track signal lost: stopping the train with the service brake.");
        }

        // 3. Authority protection (always automatic).
        var authority = AuthorityProtection.Evaluate(v, remainingAuthority, dt, vehicle, policy);
        var distanceToAuthorityBrakePoint = AuthorityProtection.DistanceToBrakePoint(v, remainingAuthority, dt, vehicle, policy);

        // 4. Emergency-brake latch.
        var emergency = _brakes.Evaluate(
            driver.EmergencyBrakeRequested,
            model.PassengerEmergencyBrakeRequested,
            authority.EmergencyBrake,
            signalLossEmergency,
            driver.EmergencyBrakeResetRequested);

        if (emergency.ResetRejected)
        {
            alerts.Add($"Emergency brake reset rejected: {emergency.ResetRejectedReason}.");
        }

        // 5. Station guidance.
        var stationStoppingDistance =
            StoppingDistance.Compute(v, vehicle.ServiceBrakeDecelerationMetersPerSecondSquared)
            + policy.StationBrakingMarginMeters;

        var atStation = _station.HasValidDistance
            && Math.Abs(_station.DistanceToNextStationMeters) <= policy.StationDistanceThresholdMeters
            && stopped;

        double? distanceToBrakePoint = null;
        var stationBrakingDue = false;
        if (_station.HasValidDistance && !_station.StationServiced)
        {
            // Latest braking point, looking one control period ahead (discretization, not a margin).
            distanceToBrakePoint = _station.DistanceToNextStationMeters - v * dt - stationStoppingDistance;
            stationBrakingDue = !atStation && distanceToBrakePoint <= 0.0;
        }

        var stationBrakingAdvised = !automatic && stationBrakingDue;
        var stationBrakingActive = automatic && stationBrakingDue;
        if (stationBrakingAdvised)
        {
            alerts.Add($"Brake now for {StationLabel()}.");
        }

        // 6. Arrival and dwell.
        var dwellOpenLeft = false;
        var dwellOpenRight = false;
        var dwelling = false;
        if (atStation && !_station.StationServiced)
        {
            if (!_station.IsDwelling)
            {
                stationEvent = StationEvent.Arrived;
            }

            if (automatic)
            {
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
            else
            {
                // Manual: arrival ends the braking guidance; doors are the driver's.
                _station.CompleteService();
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

        // 7. Doors.
        var doors = DoorController.Decide(driver.Mode, stopped, dwellOpenLeft, dwellOpenRight, driver);
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

        // 8. Overspeed and holding brake.
        var overspeed = OverspeedProtection.IsOverspeed(v, limit);
        if (overspeed)
        {
            alerts.Add("Overspeed: service brake applied.");
        }

        // Effective target. The remaining-authority braking curve is a PREVENTIVE target limit
        // (the PI never aims faster than the train can stop within its authority);
        // AuthorityProtection (step 3) is the INDEPENDENT safety backstop that brakes regardless
        // of the target. Neither replaces the other.
        var serviceDecel = vehicle.ServiceBrakeDecelerationMetersPerSecondSquared;
        var selector = new TargetSpeedSelector()
            .Limit(signalLost ? 0.0 : model.AuthorizedSpeedMetersPerSecond,
                signalLost ? TargetSpeedConstraint.TrackSignalLoss : TargetSpeedConstraint.AuthorizedSpeed)
            .Limit(vehicle.MaxSpeedMetersPerSecond, TargetSpeedConstraint.VehicleMaximum)
            .Limit(
                TargetSpeedSelector.BrakingCurveSpeed(remainingAuthority - policy.AuthorityBrakingMarginMeters, serviceDecel),
                TargetSpeedConstraint.RemainingAuthority);

        if (!automatic)
        {
            // Manual: the request is only ONE constraint; it can never raise the target.
            selector.Limit(driver.RequestedSpeedMetersPerSecond, TargetSpeedConstraint.DriverRequest);
        }
        else if (stationBrakingActive || dwelling)
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

        if (emergency.EmergencyBrakeApplied)
        {
            selector.Limit(0.0, TargetSpeedConstraint.EmergencyBrake);
        }

        if (driver.ServiceBrakeRequested)
        {
            // The driver is braking: the controller must not aim for any speed while the brake is
            // held. The request setting itself is untouched and returns as the target on release.
            selector.Limit(0.0, TargetSpeedConstraint.DriverServiceBrake);
        }

        var target = selector.Result;
        var effectiveTarget = target.MetersPerSecond;
        var holdingBrake = stopped
            && (effectiveTarget <= policy.StoppedSpeedThresholdMetersPerSecond || doorsPhysicallyOpen || doorsCommandedOpen);

        if (authority.Active)
        {
            alerts.Add(authority.EmergencyBrake
                ? "Authority overrun risk: emergency brake applied."
                : "Authority protection: service brake applied.");
        }

        if (emergency.EmergencyBrakeApplied)
        {
            alerts.Add(DescribeEmergency(emergency.LatchedCauses, model.PassengerEmergencyBrakeRequested));
        }

        // 9. Brakes and traction.
        var serviceBrake = driver.ServiceBrakeRequested
            || authority.ServiceBrake
            || overspeed
            || stationBrakingActive
            || holdingBrake;
        var emergencyBrake = emergency.EmergencyBrakeApplied;

        double power;
        if (!serviceBrake && !emergencyBrake && !doorInterlock)
        {
            power = _speed.ComputePower(effectiveTarget, v, input.Engineer, dt, vehicle.TotalRatedPowerWatts);
        }
        else
        {
            power = 0.0;
            _speed.ResetIntegral();
        }

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
                EffectiveTargetSpeedMetersPerSecond = effectiveTarget,
                TargetLimitedBy = target.LimitedBy,
                NextStationName = _station.NextStationName,
                DistanceToNextStationMeters = _station.HasValidDistance ? _station.DistanceToNextStationMeters : null,
                PlatformSide = _station.PlatformSide,
                RemainingAuthorityMeters = remainingAuthority,
                AuthorityRecalibrated = authorityRecalibrated,
                ServiceBrakeStoppingDistanceMeters = stationStoppingDistance,
                DistanceToStationBrakePointMeters = distanceToBrakePoint,
                DistanceToAuthorityBrakePointMeters = distanceToAuthorityBrakePoint,
                StationBrakingAdvised = stationBrakingAdvised,
                StationBrakingActive = stationBrakingActive,
                AtStation = atStation,
                StationEvent = stationEvent,
                TractionState = traction,
                AuthorityProtectionActive = authority.Active,
                OverspeedProtectionActive = overspeed,
                DoorInterlockActive = doorInterlock,
                EmergencyBrakeCauses = emergency.LatchedCauses,
                EmergencyBrakeLatched = emergency.EmergencyBrakeApplied,
                EmergencyBrakeResetRejected = emergency.ResetRejected,
                EmergencyBrakeResetBlockedReason = emergency.ResetBlockedReason,
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

        if (causes.HasFlag(EmergencyBrakeCause.TrackSignalLoss))
        {
            parts.Add("track signal loss");
        }

        return $"Emergency brake applied ({string.Join(", ", parts)}). Driver reset required.";
    }
}
