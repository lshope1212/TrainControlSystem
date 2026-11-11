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
    private readonly OrderedPipeSender _sender = new();

    /// <summary>To the Track Model side (throttled by the caller): position, speed, passengers.</summary>
    public void PublishTrackSide(TrainState s)
    {
        // Raw position + actual speed (the Track Model resolves occupancy from position).
        _sender.Enqueue(NamedPipeNames.TrackController, new TrainStateMessage
        {
            TrainId = s.TrainId,
            PositionMeters = s.PositionMeters,
            SpeedMetersPerSecond = s.VelocityMetersPerSecond,
            AccelerationMetersPerSecondSquared = s.AccelerationMetersPerSecondSquared,
        });
        if (s.LastBoarded != 0 || s.LastAlighted != 0 || s.LastLeftBehind != 0)
        {
            _sender.Enqueue(NamedPipeNames.TrackController, new PassengerExchangeMessage
            {
                TrainId = s.TrainId,
                Boarded = s.LastBoarded,
                Alighted = s.LastAlighted,
                LeftBehind = s.LastLeftBehind,
            });
        }
    }

    /// <summary>
    /// To the Train Controller, after EVERY 0.1 s step (state-driven timing): one consolidated
    /// status tagged with this Train Model's step count. The controller answers with commands
    /// carrying the same TickId; this model never waits for them and simply uses the latest
    /// commands it has at each step. Door/light states are not sent (they follow the commands);
    /// the commanded (not remaining) authority is relayed.
    /// </summary>
    public void PublishToController(TrainState s, long tickId, bool isActive, bool passengerRequested)
    {
        // Latest state per train only: a slow controller gets fresh states, never a backlog.
        _sender.EnqueueLatest(NamedPipeNames.TrainController, s.TrainId, new TrainModelStatusMessage
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

    /// <summary>Tells the Train Controller that this model was reset, so it clears its state too.</summary>
    public void PublishReset(long resetId) =>
        _sender.Enqueue(NamedPipeNames.TrainController, new ResetMessage { ResetId = resetId });

    private static TrainControl.Contracts.Enums.PlatformSide ParsePlatformSide(string side) =>
        side switch
        {
            "Left" => TrainControl.Contracts.Enums.PlatformSide.Left,
            "Right" => TrainControl.Contracts.Enums.PlatformSide.Right,
            "Both" => TrainControl.Contracts.Enums.PlatformSide.Both,
            _ => TrainControl.Contracts.Enums.PlatformSide.None,
        };
}
