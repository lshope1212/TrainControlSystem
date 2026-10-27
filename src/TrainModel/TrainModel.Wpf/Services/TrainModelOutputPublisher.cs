using TrainControl.Common.Communication;
using TrainControl.Contracts.Messages;
using TrainModel.Core.Models;

namespace TrainModel.Wpf.Services;

/// <summary>
/// Publishes each train's outputs to its neighbours over named pipes:
///   - to the Track Model: actual speed, occupancy, passenger exchange;
///   - to the Train Controller: actual speed + the passed-through track data and statuses.
///
/// Sends are best-effort and fire-and-forget: if a neighbour (or the TestUI standing
/// in for it) is not listening, the send simply fails and is ignored. All values are
/// metric on the wire. Publishing is throttled by the caller to avoid flooding.
/// </summary>
public sealed class TrainModelOutputPublisher
{
    /// <summary>Publish one train's current state to both neighbour endpoints.</summary>
    public void Publish(TrainState s)
    {
        // ---- To Track Model ----
        FireAndForget(NamedPipeNames.TrackController, new TrainStateMessage
        {
            TrainId = s.TrainId,
            PositionMeters = s.PositionMeters,
            SpeedMetersPerSecond = s.VelocityMetersPerSecond,
            AccelerationMetersPerSecondSquared = s.AccelerationMetersPerSecondSquared,
        });

        if (s.LastBoarded != 0 || s.LastAlighted != 0 || s.LastLeftBehind != 0)
        {
            FireAndForget(NamedPipeNames.TrackController, new PassengerExchangeMessage
            {
                TrainId = s.TrainId,
                Boarded = s.LastBoarded,
                Alighted = s.LastAlighted,
                LeftBehind = s.LastLeftBehind,
            });
        }

        // ---- To Train Controller ----
        FireAndForget(NamedPipeNames.TrainController, new TrainStateMessage
        {
            TrainId = s.TrainId,
            PositionMeters = s.PositionMeters,
            SpeedMetersPerSecond = s.VelocityMetersPerSecond,
            AccelerationMetersPerSecondSquared = s.AccelerationMetersPerSecondSquared,
        });

        FireAndForget(NamedPipeNames.TrainController, new TrainCabinStatusMessage
        {
            TrainId = s.TrainId,
            LeftDoorOpen = s.LeftDoorOpen,
            RightDoorOpen = s.RightDoorOpen,
            ExteriorLightsOn = s.ExteriorLightsOn,
            CabinTemperatureCelsius = s.CabinTemperatureCelsius,
        });

        // Pass-through + status bundle: the track data the train relays to the
        // controller, plus the passenger emergency brake state.
        FireAndForget(NamedPipeNames.TrainController, new TrainToControllerMessage
        {
            TrainId = s.TrainId,
            CommandedSpeedMetersPerSecond = s.CommandedSpeedMetersPerSecond,
            CommandedAuthorityMeters = s.CommandedAuthorityMeters,
            TrackSignalStatus = s.TrackSignal == SignalAvailability.Acquired ? "Acquired" : "Lost",
            BeaconStationName = s.BeaconStationName,
            BeaconDistanceToStopMeters = s.BeaconDistanceToStopMeters,
            BeaconPlatformSide = s.BeaconPlatformSide,
            PassengerEmergencyBrakeStatus = s.PassengerEmergencyBrakeActive ? "Engaged"
                : s.PassengerEmergencyBrakePending ? "Pending" : "Released",
        });
    }

    private static void FireAndForget<TMessage>(string pipeName, TMessage message)
        where TMessage : class
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await NamedPipeTransport.SendAsync(pipeName, message);
            }
            catch
            {
                // No listener / transient failure: ignore. The train runs standalone
                // whether or not a neighbour (or the TestUI) is capturing.
            }
        });
    }
}
