using TrainControl.Common.Communication;
using TrainControl.Contracts.Messages;
using TrainModel.Core.Models;

namespace TrainModel.Wpf.Services;

/// <summary>
/// Publishes each train's outputs to its neighbours over named pipes:
///   - to the Track Model: cumulative position, speed and train length after every physics tick
///     (<see cref="TrainStateMessage"/>), and passenger exchanges;
///   - to the Train Controller: actual speed + the passed-through track data and statuses.
///
/// Sends are ordered per destination and bounded: when a neighbour (or the TestUI standing in for it)
/// is slow or absent, only the newest state per train is kept instead of a backlog, so neither the UI
/// nor the simulation ever waits on a pipe. All values are metric on the wire.
/// </summary>
public sealed class TrainModelOutputPublisher
{
    private readonly OrderedPipeSender _sender = new();
    private readonly string _trackModelPipe;
    private readonly string _trainControllerPipe;

    /// <param name="trackModelPipe">The Track Model's inbound pipe; <see cref="NamedPipeNames.TrackModel"/> unless a test isolates it.</param>
    /// <param name="trainControllerPipe">The Train Controller's inbound pipe; <see cref="NamedPipeNames.TrainController"/> unless a test isolates it.</param>
    public TrainModelOutputPublisher(string? trackModelPipe = null, string? trainControllerPipe = null)
    {
        _trackModelPipe = string.IsNullOrWhiteSpace(trackModelPipe) ? NamedPipeNames.TrackModel : trackModelPipe;
        _trainControllerPipe = string.IsNullOrWhiteSpace(trainControllerPipe) ? NamedPipeNames.TrainController : trainControllerPipe;
    }

    /// <summary>
    /// The train's position report to the Track Model, after every 0.1 s physics tick and once at
    /// activation. <paramref name="sequence"/> is the physics tick count. Only the newest report per train
    /// is queued: the position is cumulative, so the Track Model reconstructs every boundary between two
    /// reports even if intermediate ones were coalesced. No block or map coordinate is ever sent.
    /// </summary>
    public void PublishPosition(TrainState s, long sequence, string dispatchRequestId)
    {
        _sender.EnqueueLatest(_trackModelPipe, s.TrainId, new TrainStateMessage
        {
            TrainId = s.TrainId,
            PositionMeters = s.PositionMeters,
            SpeedMetersPerSecond = s.VelocityMetersPerSecond,
            AccelerationMetersPerSecondSquared = s.AccelerationMetersPerSecondSquared,
            // From the train's own specification (TrainSpecs), never a separate constant.
            TrainLengthMeters = s.TrainLengthMeters,
            Sequence = sequence,
            DispatchRequestId = dispatchRequestId ?? string.Empty,
            PassengerCount = s.PassengerCount,
        });
    }

    /// <summary>One passenger exchange, sent once when it happens (to the Track Model).</summary>
    public void PublishPassengerExchange(TrainState s) =>
        _sender.Enqueue(_trackModelPipe, new PassengerExchangeMessage
        {
            TrainId = s.TrainId,
            ExchangeId = s.LastExchangeId,
            StationContext = s.LastExchangeStation,
            Boarded = s.LastBoarded,
            Alighted = s.LastAlighted,
            LeftBehind = s.LastLeftBehind,
        });

    /// <summary>
    /// To the Train Controller, after EVERY 0.1 s step (state-driven timing): one consolidated
    /// status tagged with this Train Model's step count. The controller answers with commands
    /// carrying the same TickId; this model never waits for them and simply uses the latest
    /// commands it has at each step. Door/light states are not sent (they follow the commands);
    /// the commanded (not remaining) authority is relayed. <paramref name="isActive"/> is true while
    /// the train is dispatched and in service; <paramref name="passengerRequested"/> is the raw passenger
    /// emergency-brake request (the controller decides whether to command the brake).
    /// </summary>
    public void PublishToController(TrainState s, long tickId, bool isActive, bool passengerRequested, long beaconSequence = 0)
    {
        // Latest state per train only: a slow controller gets fresh states, never a backlog.
        _sender.EnqueueLatest(_trainControllerPipe, s.TrainId, new TrainModelStatusMessage
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
            BeaconSequence = beaconSequence,
            NextStationName = s.BeaconStationName,
            DistanceToNextStationMeters = s.BeaconDistanceToStopMeters,
            PlatformSide = ParsePlatformSide(s.BeaconPlatformSide),
            PassengerEmergencyBrakeRequested = passengerRequested,
            CabinTemperatureCelsius = s.CabinTemperatureCelsius,
        });
    }

    /// <summary>Tells the Train Controller that this model was reset, so it clears its state too.</summary>
    public void PublishReset(long resetId) =>
        _sender.Enqueue(_trainControllerPipe, new ResetMessage { ResetId = resetId });

    private static TrainControl.Contracts.Enums.PlatformSide ParsePlatformSide(string side) =>
        side switch
        {
            "Left" => TrainControl.Contracts.Enums.PlatformSide.Left,
            "Right" => TrainControl.Contracts.Enums.PlatformSide.Right,
            "Both" => TrainControl.Contracts.Enums.PlatformSide.Both,
            _ => TrainControl.Contracts.Enums.PlatformSide.None,
        };
}
