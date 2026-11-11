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
    /// <summary>
    /// Publish one train's current state to both neighbour endpoints.
    /// <paramref name="tickId"/> is the controller tick this state answers (echoed back for
    /// correlation); <paramref name="isActive"/> is true while the train is dispatched and in
    /// service; <paramref name="passengerRequested"/> is the raw passenger emergency-brake
    /// request (the controller decides whether to command the brake — the Train Model does not).
    /// </summary>
    public void Publish(TrainState s, long tickId, bool isActive, bool passengerRequested)
    {
        // ---- To Track Model ----
        // Raw position + actual speed (the Track Model resolves occupancy from position).
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
        // One consolidated status message (replaces the old TrainState + CabinStatus +
        // TrainToController trio). Door/light states are NOT sent (the controller commands
        // them). Commanded (not remaining) authority is relayed. TrafficLight is left Unknown:
        // the controller sources signal aspect from the on-board Driver, not the Train Model.
        FireAndForget(NamedPipeNames.TrainController, new TrainModelStatusMessage
        {
            TrainId = s.TrainId,
            TickId = tickId,
            IsActive = isActive,
            ActualSpeedMetersPerSecond = s.VelocityMetersPerSecond,
            CommandedSpeedMetersPerSecond = s.CommandedSpeedMetersPerSecond,
            CommandedAuthorityMeters = s.CommandedAuthorityMeters,
            TrackSignalValid = s.TrackSignal == SignalAvailability.Acquired,
            // TrafficLight intentionally left at SignalState.Unknown (not sourced by Train Model).
            BeaconValid = !string.IsNullOrEmpty(s.BeaconStationName),
            NextStationName = s.BeaconStationName,
            DistanceToNextStationMeters = s.BeaconDistanceToStopMeters,
            PlatformSide = ParsePlatformSide(s.BeaconPlatformSide),
            PassengerEmergencyBrakeRequested = passengerRequested,
            CabinTemperatureCelsius = s.CabinTemperatureCelsius,
        });
    }

    private static TrainControl.Contracts.Enums.PlatformSide ParsePlatformSide(string side) =>
        side switch
        {
            "Left" => TrainControl.Contracts.Enums.PlatformSide.Left,
            "Right" => TrainControl.Contracts.Enums.PlatformSide.Right,
            "Both" => TrainControl.Contracts.Enums.PlatformSide.Both,
            _ => TrainControl.Contracts.Enums.PlatformSide.None,
        };

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
