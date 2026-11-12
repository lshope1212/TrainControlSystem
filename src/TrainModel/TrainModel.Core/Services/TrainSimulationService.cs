using TrainModel.Core.Interfaces;
using TrainModel.Core.Models;

namespace TrainModel.Core.Services;

/// <summary>
/// Real implementation of the Train Model plant. Owns the <see cref="Train"/>,
/// resolves faults and latches, drives <see cref="PhysicsService"/> each tick, and
/// produces an immutable <see cref="TrainState"/> snapshot. All internal units SI.
/// </summary>
public class TrainSimulationService : ITrainSimulationService
{
    private readonly PhysicsService _physics = new();
    private TrainInputs _inputs = new();
    private readonly Random _rng = new();

    public TrainSimulationService()
        : this(new Train { Id = "TRAIN-001" })
    {
    }

    public TrainSimulationService(Train train)
    {
        Train = train ?? throw new ArgumentNullException(nameof(train));
        Train.Passengers.Capacity = Train.Specs.PassengerCapacity;
        CurrentState = BuildState();
    }

    public Train Train { get; }

    public TrainState CurrentState { get; private set; }

    public void ApplyInputs(TrainInputs inputs)
    {
        _inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
    }

    public void Step(TimeSpan delta)
    {
        double dt = delta.TotalSeconds;
        if (dt <= 0.0)
        {
            return;
        }

        // ---- Resolve latched fault state from Murphy inputs ----
        Train.EngineFailed = _inputs.EngineFailure;
        Train.BrakeFailed = _inputs.BrakeFailure;
        Train.SignalPickupFailed = _inputs.SignalPickupFailure;

        // ---- Emergency-brake latch ----
        // A passenger request OR a controller emergency command engages it. Once
        // engaged it STAYS engaged until the controller clears it (passenger cannot).
        // Passenger request only raises a PENDING flag — it never engages the brake.
        if (_inputs.PassengerEmergencyBrakeRequest)
        {
            Train.PassengerEmergencyBrakePending = true;
        }

        // The Train Controller rules on the pending request. Both APPROVE and DENY simply
        // clear the pending flag — neither latches the brake here. On approval, the Train
        // Controller (Test UI) applies the emergency brake through its normal command
        // (EmergencyBrakeApplied = true), so the brake is represented by that MOMENTARY
        // command, not a separate latch. This keeps a single source of truth for the
        // emergency brake (the controller's command) and lets the controller release it
        // by clearing the command. Honored only when a request is actually pending.
        // No approval handshake any more: the request is acknowledged as soon as the Train
        // Controller applies the emergency brake (approve/deny flags kept for the TestUI).
        if (Train.PassengerEmergencyBrakePending &&
            (_inputs.EmergencyBrakeApplied || _inputs.ControllerApproveEmergencyBrake || _inputs.ControllerDenyEmergencyBrake))
        {
            Train.PassengerEmergencyBrakePending = false;
        }

        // NOTE: the controller's DIRECT emergency brake (_inputs.EmergencyBrakeApplied)
        // is MOMENTARY — it is applied while commanded and released when the command
        // clears. It is therefore NOT latched into EmergencyBrakeEngaged here; instead
        // the physics and state treat the EFFECTIVE emergency brake as
        //   EmergencyBrakeEngaged (latched, passenger-approved)  OR  _inputs.EmergencyBrakeApplied (momentary).
        // Only the passenger-approved brake latches (engaged above) and requires a
        // controller clear/reset to release. This lets the Test UI (or real controller)
        // release a direct emergency-brake command simply by clearing it.

        // ---- Advance the plant physics (power -> speed -> position) ----
        _physics.Step(Train, _inputs, dt);


        // ---- Non-kinematic actual states follow their commands ----
        // Doors only actuate when effectively stopped (a safety nicety).
        bool stopped = Train.VelocityMetersPerSecond < 0.05;
        Train.LeftDoorOpen = _inputs.LeftDoorCommandOpen && stopped;
        Train.RightDoorOpen = _inputs.RightDoorCommandOpen && stopped;
        Train.ExteriorLightsOn = _inputs.ExteriorLightCommandOn;

        // First-order cabin temperature drift toward the setpoint.
        double tempError = _inputs.CabinSetpointCelsius - Train.CabinTemperatureCelsius;
        Train.CabinTemperatureCelsius += tempError * Math.Min(1.0, 0.05 * dt);

        // ---- Automatic station passenger exchange ----
        // The beacon (sent on a station approach) gives the distance from the beacon to the
        // stop point. When we read a beacon with a positive distance, we ARM an absolute stop
        // target = our position now + that distance. When the train later reaches that target
        // AND is stopped, we exchange ONCE (random alighting bounded by onboard; boarding from
        // the Track Model's platform demand). A new beacon re-arms a fresh target. The >0 filter
        // ignores a post-station / departure beacon regardless of how the Track Model marks it.
        UpdateStationExchange(stopped);

        CurrentState = BuildState();
    }

    /// <summary>Arm the stop target from the current beacon and fire one exchange on arrival.</summary>
    private void UpdateStationExchange(bool stopped)
    {
        double beaconDist = _inputs.Beacon?.DistanceToStopMeters ?? 0.0;

        // Arm (or re-arm) when a beacon with a real forward distance names a NEW target.
        if (beaconDist > 0.0)
        {
            double target = Train.PositionMeters + beaconDist;
            // Treat it as a new beacon only if the target moved meaningfully (avoids re-arming
            // every tick off the same standing beacon value).
            if (Train.StationStopTargetMeters is not { } current || Math.Abs(current - target) > 0.5)
            {
                Train.StationStopTargetMeters = target;
                Train.HasExchangedAtArmedStop = false;
            }
        }

        // Fire once when we've reached the armed stop point and are stopped there.
        if (Train.StationStopTargetMeters is { } stopTarget
            && !Train.HasExchangedAtArmedStop
            && stopped
            && Train.PositionMeters >= stopTarget)
        {
            int onboard = Train.Passengers.PassengerCount;
            int alighting = onboard > 0 ? _rng.Next(0, onboard + 1) : 0;   // [0, onboard]; all may leave
            int waitingToBoard = Math.Max(0, _inputs.PassengerDemand);     // Track Model's platform demand

            ExchangePassengers(waitingToBoard, alighting);                 // stamps id + station, bounds by capacity
            Train.HasExchangedAtArmedStop = true;
            StationExchangeOccurred = true;                                // signals the VM to publish this tick
        }
    }

    /// <summary>Set true in the tick an automatic exchange fired; the VM reads and clears it to publish.</summary>
    public bool StationExchangeOccurred { get; set; }

    public (int boarded, int alighted) ExchangePassengers(int waitingToBoard, int alighting)
    {
        if (waitingToBoard < 0) waitingToBoard = 0;
        if (alighting < 0) alighting = 0;

        var p = Train.Passengers;

        // Alight first (frees capacity), bounded by who is actually onboard.
        int actuallyAlighting = Math.Min(alighting, p.PassengerCount);
        p.PassengerCount -= actuallyAlighting;

        // Board up to remaining capacity. If the Track Model offered more waiting
        // passengers than will fit, board only what fits and record the remainder
        // as "left behind" so the overflow is explicit rather than silently dropped.
        int freeSpace = Math.Max(0, p.Capacity - p.PassengerCount);
        int actuallyBoarding = Math.Min(waitingToBoard, freeSpace);
        p.PassengerCount += actuallyBoarding;

        int leftBehind = waitingToBoard - actuallyBoarding;   // >= 0

        p.LastBoarded = actuallyBoarding;
        p.LastAlighted = actuallyAlighting;
        p.LastLeftBehind = leftBehind;
        p.WasAtCapacity = leftBehind > 0;

        // Stamp a unique id + the station for this exchange so the Track Model can dedupe
        // resends and reject a delayed report that arrives for the wrong station.
        p.LastExchangeId = System.Guid.NewGuid();
        p.LastExchangeStation = _inputs.Beacon?.StationName ?? string.Empty;

        CurrentState = BuildState();
        return (actuallyBoarding, actuallyAlighting);
    }

    public void ClearEmergencyBrakeFromController()
    {
        Train.EmergencyBrakeEngaged = false;
        Train.PassengerEmergencyBrakePending = false;
    }

    public void PlaceAtDispatchOrigin()
    {
        Train.PositionMeters = 0;
        Train.VelocityMetersPerSecond = 0;
        Train.AccelerationMetersPerSecondSquared = 0;
        Train.PreviousAccelerationMetersPerSecondSquared = 0;
        CurrentState = BuildState();
    }

    private TrainState BuildState()
    {
        // Signal availability: lost if a pickup failure is injected, else whatever
        // the track circuit reported. (No positive "broken" signal exists — loss
        // is the absence of valid data.)
        SignalAvailability signal = Train.SignalPickupFailed
            ? SignalAvailability.Lost
            : _inputs.TrackSignal;

        return new TrainState
        {
            TrainId = Train.Id,

            PositionMeters = Train.PositionMeters,
            VelocityMetersPerSecond = Train.VelocityMetersPerSecond,
            AccelerationMetersPerSecondSquared = Train.AccelerationMetersPerSecondSquared,

            FrontPositionMeters = Train.PositionMeters,
            TrainLengthMeters = Train.Specs.LengthMeters,
            LineName = Train.LineName,
            OperatingStatus = Train.OperatingStatus,

            CommandedSpeedMetersPerSecond = _inputs.CommandedSpeedMetersPerSecond,
            CommandedAuthorityMeters = _inputs.CommandedAuthorityMeters,
            TrackSignal = signal,

            BeaconStationName = _inputs.Beacon?.StationName ?? string.Empty,
            BeaconDistanceToStopMeters = _inputs.Beacon?.DistanceToStopMeters ?? 0,
            BeaconPlatformSide = _inputs.Beacon?.Side.ToString() ?? "None",
            StationAnnouncement = _inputs.StationAnnouncement,
            GradePercent = _inputs.GradePercent,

            ServiceBrakeEngaged = _inputs.ServiceBrakeApplied && !Train.BrakeFailed,
            EmergencyBrakeEngaged = Train.EmergencyBrakeEngaged || _inputs.EmergencyBrakeApplied,
            LeftDoorOpen = Train.LeftDoorOpen,
            RightDoorOpen = Train.RightDoorOpen,
            ExteriorLightsOn = Train.ExteriorLightsOn,
            CabinTemperatureCelsius = Train.CabinTemperatureCelsius,

            EngineFailed = Train.EngineFailed,
            BrakeFailed = Train.BrakeFailed,
            SignalPickupFailed = Train.SignalPickupFailed,
            PassengerEmergencyBrakeActive = Train.EmergencyBrakeEngaged,
            PassengerEmergencyBrakePending = Train.PassengerEmergencyBrakePending,

            PassengerCount = Train.Passengers.PassengerCount,
            LastBoarded = Train.Passengers.LastBoarded,
            LastAlighted = Train.Passengers.LastAlighted,
            LastLeftBehind = Train.Passengers.LastLeftBehind,
            LastExchangeId = Train.Passengers.LastExchangeId,
            LastExchangeStation = Train.Passengers.LastExchangeStation,
            WasAtCapacity = Train.Passengers.WasAtCapacity,
        };
    }
}
