using TrackModel.Core.Models;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;
namespace TrackModel.Core.Interfaces;

public interface ITrackService
{
    TrackLayout Layout { get; }
    TimeSpan SystemTime { get; }
    int LayoutRevision { get; }
    event EventHandler? StateChanged;
    /// <summary>One atomic occupancy step of a train, raised while the layout shows exactly that step.</summary>
    event EventHandler<TrackOccupancyTransitionEventArgs>? OccupancyTransition;
    /// <summary>Placement, fault and retirement of trains tracked from position telemetry.</summary>
    event EventHandler<TrainTrackingEventArgs>? TrainTrackingChanged;
    IReadOnlyList<TrainTrackingSnapshot> TrackedTrains { get; }
    TrainTrackingSnapshot? FindTrackedTrain(string trainId);
    TrainPlacementResult RequestTrainPlacement(TrainDispatchRequestMessage request);
    bool CancelPendingPlacement(string dispatchRequestId, string reason);
    TrainPositionResult ApplyTrainPosition(TrainStateMessage message);
    TrainPlacementResult UpdateTrainRoute(TrainRouteUpdateMessage update);
    bool RetireTrain(string trainId, string reason);
    TrackBlock? FindBlock(string blockId);
    void LoadLayout(TrackLayout layout);
    void ApplyCommand(TrackModelCommandMessage message);
    IReadOnlyList<string> ApplyCommands(IEnumerable<TrackModelCommandMessage> messages);
    IReadOnlyList<string> ApplyCommandBatch(TrackModelCommandBatchMessage batch, DateTimeOffset receivedAtUtc);
    int ExpireCommands(DateTimeOffset now);
    IReadOnlyDictionary<string, (string BlockId, double Speed, double Authority, string RequestId)> CaptureTrainCodes();
    void ApplyTrainUpdate(TrackModelTrainUpdateMessage message);
    void ApplyFailures(TrackModelFailureCommandMessage message);
    void ApplyTemperature(TrackModelTemperatureCommandMessage message);
    void ApplyPassengerDemand(TrackModelPassengerDemandMessage message);
    void SetSystemTime(TimeSpan time);
    void SetMaintenance(string blockId, MaintenanceState state);
    TrackLayoutMessage CreateLayoutMessage();
    TrackModelBlockStateMessage CreateBlockState(string blockId);
    TrackModelTrainEnvironmentMessage CreateTrainEnvironment(string blockId);
    TicketSalesMessage CreateTicketSales(string lineId);
}
