using TrainControl.Contracts.Messages;
using TrainModel.Core.Models;

namespace TrainModel.Core.Mapping;

/// <summary>
/// Builds outbound contract messages (metric on the wire) from the Train Model's
/// state, and folds inbound contract messages into a <see cref="TrainInputs"/>.
/// Keeps the plant free of transport concerns.
/// </summary>
public static class TrainMessageMapper
{
    // ---------------- Outbound: state -> messages ----------------

    public static TrainStateMessage ToTrainState(TrainState s) => new()
    {
        TrainId = s.TrainId,
        PositionMeters = s.PositionMeters,
        SpeedMetersPerSecond = s.VelocityMetersPerSecond,
        AccelerationMetersPerSecondSquared = s.AccelerationMetersPerSecondSquared,
    };

    public static TrainCabinStatusMessage ToCabinStatus(TrainState s) => new()
    {
        TrainId = s.TrainId,
        LeftDoorOpen = s.LeftDoorOpen,
        RightDoorOpen = s.RightDoorOpen,
        ExteriorLightsOn = s.ExteriorLightsOn,
        CabinTemperatureCelsius = s.CabinTemperatureCelsius,
    };

    public static PassengerExchangeMessage ToPassengerExchange(TrainState s) => new()
    {
        TrainId = s.TrainId,
        Boarded = s.LastBoarded,
        Alighted = s.LastAlighted,
        LeftBehind = s.LastLeftBehind,
    };

    // ---------------- Inbound: messages -> inputs ----------------

    public static void ApplyCommand(TrainInputs inputs, TrainCommandMessage m)
    {
        inputs.PowerCommandWatts = m.PowerCommandWatts;
        inputs.ServiceBrakeApplied = m.ServiceBrakeApplied;
        inputs.EmergencyBrakeApplied = m.EmergencyBrakeApplied;
        inputs.LeftDoorCommandOpen = m.LeftDoorOpen;
        inputs.RightDoorCommandOpen = m.RightDoorOpen;
        inputs.ExteriorLightCommandOn = m.ExteriorLightsOn;
        inputs.CabinSetpointCelsius = m.CabinSetpointCelsius;
        inputs.StationAnnouncement = m.StationAnnouncement;
    }

    public static void ApplyTrackCondition(TrainInputs inputs, TrackConditionMessage m)
    {
        inputs.GradePercent = m.GradePercent;
    }

    public static void ApplyFailures(TrainInputs inputs, TrainFailureMessage m)
    {
        inputs.EngineFailure = m.EngineFailure;
        inputs.BrakeFailure = m.BrakeFailure;
        inputs.SignalPickupFailure = m.SignalPickupFailure;
    }
}
