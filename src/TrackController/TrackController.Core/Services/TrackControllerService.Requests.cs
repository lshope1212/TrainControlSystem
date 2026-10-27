using TrackController.Contracts.Messages;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Core.Services;

/// <summary>Evaluates track conditions and rejects movement or equipment commands that violate protection rules.</summary>
public sealed partial class TrackControllerService
{
    /// <summary>Validates the request origin and route before evaluating conservative movement limits.</summary>
    public void SubmitMovement(MovementSuggestionMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(message.TargetBlockId) || string.IsNullOrWhiteSpace(message.RequestId) || message.RequestId.Length > 128 || message.TrainId is null || message.TrainId.Length > 128)
            {
                throw new ArgumentException("Provide a target block and request ID (up to 128 characters). The train label is optional.");
            }
            GetOwnedBlock(message.TargetBlockId);

            if (!message.IsActive)
            {
                if (_requests.TryGetValue(message.TargetBlockId, out var current) && current.Value.RequestId != message.RequestId)
                {
                    throw new ArgumentException("Cancellation must identify the current request ID for this target block.");
                }

                _requests.Remove(message.TargetBlockId);
                Reevaluate();
                return;
            }

            if (_retiredRequests.Contains(message.RequestId))
            {
                throw new ArgumentException("This request's target block cleared. Submit a new request ID and current target block.");
            }

            if (message.AuthorityReference != MovementSuggestionMessage.BlockEntryReference || !double.IsFinite(message.SuggestedSpeedMetersPerSecond) || message.SuggestedSpeedMetersPerSecond < 0 || !double.IsFinite(message.SuggestedAuthorityMeters) || message.SuggestedAuthorityMeters < 0 || message.PathBlockIds is null || message.PathBlockIds.Count > _blocks.Count)
            {
                throw new ArgumentException("Use a route limit measured from the target block's entry and finite, nonnegative speed/authority.");
            }

            var path = new[]
                        {
                message.TargetBlockId
            }.Concat(message.PathBlockIds).ToArray();
            if (message.EntryBlockId is not null && !GetBlock(message.EntryBlockId).Definition.NextBlockIds.Contains(message.TargetBlockId))
            {
                throw new ArgumentException("The entry block must connect toward the target block.");
            }

            if (path.Distinct().Count() != path.Length)
            {
                throw new ArgumentException("A request cannot revisit a block.");
            }

            for (int i = 0; i < path.Length; i++)
            {
                var block = GetOwnedBlock(path[i]);
                if (i + 1 < path.Length && !block.Definition.NextBlockIds.Contains(path[i + 1]))
                {
                    throw new ArgumentException($"No permitted connection from {path[i]} to {path[i + 1]}.");
                }
            }

            if (_requests.Values.Any(request => request.Value.RequestId == message.RequestId &&
                (request.Value.TargetBlockId != message.TargetBlockId || request.Value.EntryBlockId != message.EntryBlockId ||
                 !request.Value.PathBlockIds.SequenceEqual(message.PathBlockIds))))
            {
                throw new ArgumentException("A pending request ID already identifies a different target or route. Cancel it or use a new ID.");
            }

            _requests[message.TargetBlockId] = (new MovementSuggestionMessage { RequestId = message.RequestId, TrainId = message.TrainId, TargetBlockId = message.TargetBlockId, EntryBlockId = message.EntryBlockId, PathBlockIds = [.. message.PathBlockIds], SuggestedAuthorityMeters = message.SuggestedAuthorityMeters, SuggestedSpeedMetersPerSecond = message.SuggestedSpeedMetersPerSecond }, _clock.GetUtcNow());
            Reevaluate();
        }
    }

    /// <summary>Accepts a switch request only when its complete protection zone permits movement.</summary>
    public string RequestSwitch(SwitchPositionRequestMessage message)
    {
        lock (_gate)
        {
            var block = GetOwnedBlock(message.BlockId);
            ValidateSwitchCommand(block, message.RequestedPosition);

            block.DesiredSwitch = message.RequestedPosition;
            Reevaluate();
            return $"Switch {message.BlockId}: requested {message.RequestedPosition}, commanded {block.CommandedSwitch}. Track Model must report the actual position separately.";
        }
    }

    /// <summary>Closes a clear block immediately or holds an occupied block pending safe clearance.</summary>
    public string RequestMaintenance(MaintenanceRequestMessage message)
    {
        lock (_gate)
        {
            var block = GetOwnedBlock(message.BlockId);
            if (!Enum.IsDefined(message.RequestedState))
            {
                throw new ArgumentException("Invalid maintenance state.");
            }

            block.Maintenance = message.RequestedState == MaintenanceState.Open ? "Open" : ClosurePlc(block) ? "Closed" : "Pending closure";
            Reevaluate();
            return $"Block {message.BlockId}: {block.Maintenance}.";
        }
    }

    /// <summary>Applies maintenance commands only when the corresponding equipment interlocks permit them.</summary>
    public void ApplyManual(string blockId, SwitchPosition? switchPosition = null, SignalState? signal = null, CrossingState? crossing = null)
    {
        lock (_gate)
        {
            if (!_maintenanceMode)
            {
                throw new InvalidOperationException("Enable maintenance mode before sending manual commands.");
            }

            var block = GetOwnedBlock(blockId);
            if (switchPosition is { } requestedPosition)
            {
                ValidateSwitchCommand(block, requestedPosition);
            }

            if (signal is not null && (!block.Definition.HasSignal || signal != SignalState.Red))
            {
                throw new InvalidOperationException("Manual signals may be set to Red. Proceed aspects require automatic route protection.");
            }

            if (crossing is not null && (!block.Definition.HasCrossing || crossing is not (CrossingState.Open or CrossingState.Closed) || (crossing == CrossingState.Open && CrossingRequired(block))))
            {
                throw new InvalidOperationException("Crossing cannot open while its zone is occupied, unavailable, faulted, or requested.");
            }

            if (switchPosition is { } position)
            {
                block.DesiredSwitch = position;
            }

            if (signal is not null)
            {
                block.ManualRed = true;
            }

            if (crossing is { } crossingPosition)
            {
                block.ManualCrossingClosed = crossingPosition == CrossingState.Closed;
            }

            Reevaluate();
        }
    }

    private void ValidateSwitchCommand(Block block, SwitchPosition position)
    {
        if (block.Definition.NormalNextBlockId is null || position is not (SwitchPosition.Normal or SwitchPosition.Reverse))
        {
            throw new ArgumentException("Select an owned switch and Normal or Reverse.");
        }

        if (_hardwareMode && _plc is null)
        {
            throw new InvalidOperationException($"Switch {block.Definition.BlockId}: load a valid PLC program before sending a switch command.");
        }

        if (_paused && !_maintenanceMode)
        {
            throw new InvalidOperationException($"Switch {block.Definition.BlockId}: simulation is paused. Resume or enable maintenance mode before sending a switch command.");
        }

        if (_hardwareMode && !SwitchPlc(block, position)["switch_move"][0])
        {
            throw new InvalidOperationException(GetSwitchProtectionReason(block) ?? $"Switch {block.Definition.BlockId}: PLC withholds switch movement.");
        }

        // Independent veto after the PLC decision.
        if (GetSwitchProtectionReason(block) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }
    }

    private string? GetSwitchProtectionReason(Block block)
    {
        string prefix = $"Switch {block.Definition.BlockId} locked: ";
        foreach (string id in block.Definition.SwitchProtectionBlockIds)
        {
            var zone = GetBlock(id);
            if (!IsFresh(zone))
            {
                return $"{prefix}block {id} needs fresh feedback.";
            }

            if (zone.Feedback!.Occupancy != OccupancyState.Clear)
            {
                return $"{prefix}block {id} is {(zone.Feedback.Occupancy == OccupancyState.Occupied ? "occupied" : "of unknown occupancy")}.";
            }

            if (zone.Feedback.IsFaulted || zone.Feedback.IsClosed)
            {
                return $"{prefix}block {id} reports a fault or closure.";
            }

            if (zone.Feedback.IsReservedByCtc != false)
            {
                return $"{prefix}block {id} is {(zone.Feedback.IsReservedByCtc == true ? "reserved by CTC" : "missing CTC reservation feedback")}.";
            }
        }

        var movement = _snapshot?.Authorizations.FirstOrDefault(authorization => authorization.AuthorizedAuthorityMeters > 0 && new[] { authorization.Request!.TargetBlockId }.Concat(authorization.Request!.PathBlockIds).Intersect(block.Definition.SwitchProtectionBlockIds).Any());
        return movement is null ? null : $"{prefix}active movement from block {movement.Request!.TargetBlockId} uses its protection zone.";
    }

    private bool CrossingRequired(Block block) => block.Definition.CrossingProtectionBlockIds.Any(id =>
        {
            var zone = GetBlock(id);
            return !IsFresh(zone) || zone.Feedback is not { Occupancy: OccupancyState.Clear, IsFaulted: false, IsClosed: false };
        }) || _requests.Values.Any(request => IsActive(request) && GetRequestPath(request).Contains(block.Definition.BlockId));
}
