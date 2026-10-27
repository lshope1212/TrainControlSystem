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
    TrackBlock? FindBlock(string blockId);
    void LoadLayout(TrackLayout layout);
    void ApplyCommand(TrackModelCommandMessage message);
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
