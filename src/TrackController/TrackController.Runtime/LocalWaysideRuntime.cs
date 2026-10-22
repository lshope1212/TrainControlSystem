using System.Text.Json;
using TrackController.Contracts.Messages;
using TrackController.Core.Services;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Runtime;

/// <summary>Validates and dispatches local application messages through the same runtime boundary used by the Pi.</summary>
public sealed class LocalWaysideRuntime : IWaysideRuntime
{
    private readonly TrackControllerService _controller;

    public WaysideSnapshotMessage LatestSnapshot { get; private set; }

    /// <summary>Wraps an existing controller without creating a second copy of its state.</summary>
    public LocalWaysideRuntime(TrackControllerService controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        LatestSnapshot = controller.GetSnapshot();
    }

    /// <summary>Validates and dispatches a message, then refreshes the resulting controller state.</summary>
    public Task<object> ReceiveAsync(MessageEnvelope message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string detail = "Accepted.";
            switch (message.MessageType)
            {
                case "GetSnapshot":
                    return Task.FromResult<object>(_controller.GetSnapshot());
                case nameof(MovementSuggestionMessage):
                    _controller.SubmitMovement(MessageSerializer.DeserializePayload<MovementSuggestionMessage>(message));
                    detail = "Movement evaluated; inspect authorized outputs.";
                    break;
                case nameof(TrackModelBlockStateMessage):
                    _controller.ApplyFeedback([MessageSerializer.DeserializePayload<TrackModelBlockStateMessage>(message)]);
                    break;
                case "TrackModelBlockStates":
                    _controller.ApplyFeedback(MessageSerializer.ReadValue<List<TrackModelBlockStateMessage>>(message, "blocks"));
                    break;
                case nameof(SwitchPositionRequestMessage):
                    detail = _controller.RequestSwitch(MessageSerializer.DeserializePayload<SwitchPositionRequestMessage>(message));
                    break;
                case nameof(MaintenanceRequestMessage):
                    detail = _controller.RequestMaintenance(MessageSerializer.DeserializePayload<MaintenanceRequestMessage>(message));
                    break;
                case "SetSimulation":
                    _controller.SetSimulation(MessageSerializer.ReadValue<bool>(message, "paused"), MessageSerializer.ReadValue<double>(message, "speedMultiplier"));
                    break;
                case nameof(WaysideConfigurationMessage):
                    _controller.Configure(MessageSerializer.DeserializePayload<WaysideConfigurationMessage>(message));
                    break;
                case "ApplyManual":
                    string blockId = MessageSerializer.ReadValue<string>(message, "blockId");
                    _controller.ApplyManual(blockId,
                        message.Payload.TryGetProperty("switchPosition", out var sw) && sw.ValueKind != JsonValueKind.Null ? MessageSerializer.ReadValue<SwitchPosition>(message, "switchPosition") : null,
                        message.Payload.TryGetProperty("signal", out var sig) && sig.ValueKind != JsonValueKind.Null ? MessageSerializer.ReadValue<SignalState>(message, "signal") : null,
                        message.Payload.TryGetProperty("crossing", out var cr) && cr.ValueKind != JsonValueKind.Null ? MessageSerializer.ReadValue<CrossingState>(message, "crossing") : null);
                    detail = $"Manual command applied for block {blockId}. Check Command and Reported in Wayside equipment.";
                    break;
                case "SetMaintenanceMode":
                    _controller.SetMaintenanceMode(MessageSerializer.ReadValue<bool>(message, "enabled"));
                    break;
                case "SetControllerMode":
                    _controller.SetHardwareMode(MessageSerializer.ReadValue<bool>(message, "hardwareMode"));
                    break;
                case "LoadPlc":
                    string? territoryId = message.Payload.TryGetProperty("territoryId", out var targetId) ? targetId.GetString() : null;
                    if (territoryId is not null && territoryId != _controller.GetConfiguration().WaysideId)
                    {
                        throw new ArgumentException("PLC target does not match this territory.");
                    }
                    _controller.LoadPlc(MessageSerializer.ReadValue<string>(message, "name"), MessageSerializer.ReadValue<string>(message, "source"));
                    detail = "PLC loaded. Resume explicitly.";
                    break;
                case nameof(BlockStatusMessage):
                    var block = MessageSerializer.DeserializePayload<BlockStatusMessage>(message);
                    _controller.ApplyFeedback([new TrackModelBlockStateMessage { BlockId = block.BlockId, ObservedAtUtc = DateTimeOffset.UtcNow, Occupancy = block.Occupancy, Signal = block.Signal, Switch = block.Switch, Crossing = block.Crossing }]);
                    break;
                case nameof(MovementRequestMessage):
                    throw new ArgumentException("Movement requires MovementSuggestionMessage with target block and route. A train label alone cannot identify an occupied block.");
                default:
                    throw new ArgumentException($"Unsupported message type: {message.MessageType}.");
            }

            return Task.FromResult<object>(new WaysideAcknowledgementMessage(true, detail, message.MessageType));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return Task.FromResult<object>(new WaysideAcknowledgementMessage(false, ex.Message, message.MessageType));
        }
        finally
        {
            LatestSnapshot = _controller.GetSnapshot();
        }
    }

    /// <summary>Refreshes runtime outputs so timeouts apply even when no new input arrives.</summary>
    public Task<WaysideSnapshotMessage> PollAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LatestSnapshot = _controller.GetSnapshot();
        return Task.FromResult(LatestSnapshot);
    }

    /// <summary>Pauses the controller before releasing its runtime wrapper.</summary>
    public ValueTask DisposeAsync()
    {
        _controller.SetSimulation(true, LatestSnapshot.SpeedMultiplier);
        return ValueTask.CompletedTask;
    }
}
