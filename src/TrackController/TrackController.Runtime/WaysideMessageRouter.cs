using System.Text.Json;
using TrackController.Contracts.Messages;
using TrackController.Core.Interfaces;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Runtime;

/// <summary>The sole inbound application boundary, called by the real pipe receiver in both modes.</summary>
public sealed class WaysideMessageRouter(ITrackControllerService controller)
{
    /// <summary>Deserializes and validates an application input before calling the controller service.</summary>
    public object Receive(string json)
    {
        try
        {
            return Receive(MessageSerializer.Deserialize(json));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return new WaysideAcknowledgementMessage(false, ex.Message);
        }
    }

    /// <summary>Deserializes and validates an application input before calling the controller service.</summary>
    public object Receive(MessageEnvelope envelope)
    {
        try
        {
            string detail = "Accepted.";
            switch (envelope.MessageType)
            {
                case nameof(WaysideSnapshotRequest):
                    return controller.GetSnapshot();
                case nameof(WaysideMovementRequestMessage):
                    controller.SubmitMovement(Read<WaysideMovementRequestMessage>(envelope));
                    detail = "Movement evaluated; inspect authorized outputs.";
                    break;
                case nameof(WaysideFeedbackMessage):
                    controller.ApplyFeedback(Read<WaysideFeedbackMessage>(envelope));
                    break;
                case nameof(SwitchPositionRequestMessage):
                    detail = controller.RequestSwitch(Read<SwitchPositionRequestMessage>(envelope));
                    break;
                case nameof(MaintenanceRequestMessage):
                    detail = controller.RequestMaintenance(Read<MaintenanceRequestMessage>(envelope));
                    break;
                case nameof(WaysideSimulationMessage):
                    controller.SetSimulation(Read<WaysideSimulationMessage>(envelope));
                    break;
                case nameof(WaysideConfigurationMessage):
                    controller.Configure(Read<WaysideConfigurationMessage>(envelope));
                    break;
                case nameof(WaysideManualCommandMessage):
                    controller.ApplyManual(Read<WaysideManualCommandMessage>(envelope));
                    break;
                case nameof(WaysideMaintenanceModeMessage):
                    controller.SetMaintenanceMode(Read<WaysideMaintenanceModeMessage>(envelope).IsEnabled);
                    break;
                case nameof(WaysideControllerModeMessage):
                    controller.SetHardwareMode(Read<WaysideControllerModeMessage>(envelope).IsHardwareMode);
                    break;
                case nameof(WaysidePlcProgramMessage):
                    var program = Read<WaysidePlcProgramMessage>(envelope);
                    controller.LoadPlc(program.Name, program.Source);
                    detail = "PLC loaded. Resume explicitly.";
                    break;
                case nameof(BlockStatusMessage):
                    var block = Read<BlockStatusMessage>(envelope);
                    controller.ApplyFeedback(new WaysideFeedbackMessage { Blocks = [new WaysideBlockFeedback { BlockId = block.BlockId, ObservedAtUtc = DateTimeOffset.UtcNow, Occupancy = block.Occupancy, Signal = block.Signal, Switch = block.Switch, Crossing = block.Crossing }] });
                    break;
                case nameof(TrackStateMessage):
                    var state = Read<TrackStateMessage>(envelope);
                    controller.ApplyFeedback(new WaysideFeedbackMessage { Blocks = [new WaysideBlockFeedback { BlockId = state.BlockId, ObservedAtUtc = DateTimeOffset.UtcNow, Occupancy = state.IsOccupied ? OccupancyState.Occupied : OccupancyState.Clear, Signal = state.Signal, Switch = state.Switch }] });
                    break;
                case nameof(MovementSuggestionMessage):
                case nameof(MovementRequestMessage):
                    throw new ArgumentException("Movement requires WaysideMovementRequestMessage with target block, front offset, and route. A train label alone cannot identify an occupied block.");
                default:
                    throw new ArgumentException($"Unsupported message type: {envelope.MessageType}.");
            }

            return new WaysideAcknowledgementMessage(true, detail, envelope.MessageType);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return new WaysideAcknowledgementMessage(false, ex.Message, envelope.MessageType);
        }
    }

    private static T Read<T>(MessageEnvelope envelope)
        where T : class => MessageSerializer.DeserializePayload<T>(envelope);
}
