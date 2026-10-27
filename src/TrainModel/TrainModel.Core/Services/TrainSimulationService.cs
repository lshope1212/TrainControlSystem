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
        if (Train.PassengerEmergencyBrakePending &&
            (_inputs.ControllerApproveEmergencyBrake || _inputs.ControllerDenyEmergencyBrake))
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

        CurrentState = BuildState();
    }

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

        CurrentState = BuildState();
        return (actuallyBoarding, actuallyAlighting);
    }

    public void ClearEmergencyBrakeFromController()
    {
        Train.EmergencyBrakeEngaged = false;
        Train.PassengerEmergencyBrakePending = false;
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
            WasAtCapacity = Train.Passengers.WasAtCapacity,
        };
    }
}
