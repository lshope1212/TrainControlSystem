using TrackController.Contracts.Messages;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Core.Services;

/// <summary>Evaluates track conditions and rejects movement or equipment commands that violate protection rules.</summary>
public sealed partial class TrackControllerService
{
    /// <summary>
    /// Validates the request origin and route before evaluating conservative movement limits. A
    /// request naming a <see cref="MovementSuggestionMessage.PredecessorRequestId"/> that is not yet
    /// active here is only validated and staged (<see cref="MovementSubmission.Staged"/>): it grants
    /// nothing until <see cref="ApplyFeedback"/> observes the train's transition into its target.
    /// </summary>
    public MovementSubmission SubmitMovement(MovementSuggestionMessage message) => SubmitMovementCore(message, false);

    /// <summary>Router-only route assessment. Foreign equipment commands remain forbidden.</summary>
    public MovementSubmission SubmitCoordinatedMovement(MovementSuggestionMessage message)
    {
        if (!_configuration.CoordinatedRoutes) throw new InvalidOperationException("This runtime is not configured for coordinated routes.");
        return SubmitMovementCore(message, true);
    }

    private MovementSubmission SubmitMovementCore(MovementSuggestionMessage message, bool coordinated)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(message.TargetBlockId) || string.IsNullOrWhiteSpace(message.RequestId) || message.RequestId.Length > 128 || message.TrainId is null || message.TrainId.Length > 128 || message.PredecessorRequestId is { Length: 0 or > 128 })
            {
                throw new ArgumentException("Provide a target block and request ID (up to 128 characters). The train label is optional.");
            }
            if (coordinated) GetBlock(message.TargetBlockId); else GetOwnedBlock(message.TargetBlockId);

            if (!message.IsActive)
            {
                // A staged successor is cancelled by its own ID (e.g. the route changed). Its ID is
                // retired so a delayed copy of the staging message can never re-stage it.
                if (_staged.Remove(message.RequestId))
                {
                    _retiredRequests.Add(message.RequestId);
                    Reevaluate();
                    return MovementSubmission.Cancelled;
                }

                if (_requests.TryGetValue(message.TargetBlockId, out var current) && current.Value.RequestId != message.RequestId)
                {
                    // A late or repeated cancellation of a request that was already retired (e.g. replaced
                    // by its successor at a block transition) changes nothing and is not an error.
                    if (_retiredRequests.Contains(message.RequestId))
                    {
                        return MovementSubmission.Cancelled;
                    }

                    throw new ArgumentException("Cancellation must identify the current request ID for this target block.");
                }

                if (_requests.Remove(message.TargetBlockId))
                {
                    GetBlock(message.TargetBlockId).Authority = null;
                    DiscardSuccessorsOf(message.RequestId);
                    // A cancelled route never comes back: a delayed or repeated copy of it must not
                    // replace the route CTC sent instead (and pull its switches back).
                    _cancelledRequests[message.RequestId] = current.Value;
                }

                Reevaluate();
                return MovementSubmission.Cancelled;
            }

            if (_retiredRequests.Contains(message.RequestId))
            {
                throw new RetiredMovementRequestException();
            }

            if (_cancelledRequests.TryGetValue(message.RequestId, out var cancelled))
            {
                if (cancelled.TargetBlockId == message.TargetBlockId && cancelled.EntryBlockId == message.EntryBlockId && cancelled.PathBlockIds.SequenceEqual(message.PathBlockIds ?? []))
                {
                    throw new ArgumentException("This movement request was cancelled; a copy of it cannot restore its route. Submit the current route.");
                }

                // The ID is being reused for a different route: a new movement, evaluated from scratch.
                _cancelledRequests.Remove(message.RequestId);
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
                var block = coordinated ? GetBlock(path[i]) : GetOwnedBlock(path[i]);
                if (i + 1 < path.Length && !block.Definition.NextBlockIds.Contains(path[i + 1]))
                {
                    throw new ArgumentException($"No permitted connection from {path[i]} to {path[i + 1]}.");
                }
            }

            if (_requests.Values.Concat(_staged.Values).Any(request => request.Value.RequestId == message.RequestId &&
                (request.Value.TargetBlockId != message.TargetBlockId || request.Value.EntryBlockId != message.EntryBlockId ||
                 !request.Value.PathBlockIds.SequenceEqual(message.PathBlockIds))))
            {
                throw new ArgumentException("A pending request ID already identifies a different target or route. Cancel it or use a new ID.");
            }

            var active = _requests.Values.FirstOrDefault(request => request.Value.RequestId == message.RequestId);
            if (message.PredecessorRequestId is { } predecessorId && active.Value is null)
            {
                StageSuccessor(message, predecessorId);
                Reevaluate();
                return MovementSubmission.Staged;
            }

            // A request that took over at a block transition inherited its predecessor's end point; a
            // renewal or resend can never move it further or raise its speed.
            if (active.Value?.PredecessorRequestId is not null
                && (message.SuggestedAuthorityMeters > active.Value.SuggestedAuthorityMeters + HandoffTolerance
                    || message.SuggestedSpeedMetersPerSecond > active.Value.SuggestedSpeedMetersPerSecond + HandoffTolerance))
            {
                throw new ArgumentException("This request took over its predecessor's end point at a block transition and cannot be extended. A new movement needs a new request.");
            }

            // Preserve CTC's cancel/reissue and departure semantics. The replacement is still subject
            // to the full PLC and independent checks; it does not require optional speed telemetry.
            PrepareCtcMovement(message);

            // An ordinary request, or a renewal of an active one (including a promoted successor, which
            // keeps the ID of the request it replaced for diagnostics). A successor CTC now sends as an
            // ordinary request leaves staging and is evaluated like any other request.
            _staged.Remove(message.RequestId);
            _requests[message.TargetBlockId] = (new MovementSuggestionMessage { RequestId = message.RequestId, TrainId = message.TrainId, TargetBlockId = message.TargetBlockId, EntryBlockId = message.EntryBlockId, PathBlockIds = [.. message.PathBlockIds], SuggestedAuthorityMeters = message.SuggestedAuthorityMeters, SuggestedSpeedMetersPerSecond = message.SuggestedSpeedMetersPerSecond, PredecessorRequestId = active.Value?.PredecessorRequestId }, _clock.GetUtcNow());
            Reevaluate();
            return MovementSubmission.Accepted;
        }
    }

    /// <summary>
    /// Accepts a switch request only when its complete protection zone permits movement. Every CTC
    /// reservation in the zone locks it out, including a route that needs this switch: that route's
    /// alignment is set automatically (see <see cref="ComputeSwitchDemands"/>) and supersedes this request.
    /// </summary>
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

    /// <summary>
    /// Why the switch may not move now; null when its whole protection zone permits it. Applied after
    /// the PLC decision in both modes, to automatic route alignment and to explicit switch requests.
    /// </summary>
    /// <param name="requesterId">
    /// The movement request whose route needs this switch moved: its own reservation of the zone does
    /// not lock the switch. Null for a dispatcher's or maintainer's request, which every CTC
    /// reservation in the zone locks out.
    /// </param>
    /// <param name="automatic">True for automatic route alignment, which also never moves a switch whose zone is closed or pending closure for maintenance.</param>
    private string? GetSwitchProtectionReason(Block block, string? requesterId = null, bool automatic = false)
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

            if (automatic && zone.Maintenance != "Open")
            {
                return $"{prefix}block {id} is {zone.Maintenance.ToLowerInvariant()} for maintenance.";
            }

            if (_configuration.RequireCtcReservationFeedback && zone.Feedback.IsReservedByCtc is null)
            {
                return $"{prefix}block {id} is missing CTC reservation feedback.";
            }

            if (ReservationHolder(zone, requesterId) is { } holder)
            {
                return $"{prefix}block {id} is reserved by CTC for {holder}.";
            }
        }

        var movement = _lastResults.FirstOrDefault(authorization => authorization.AuthorizedAuthorityMeters > 0 && new[] { authorization.Request!.TargetBlockId }.Concat(authorization.Request!.PathBlockIds).Intersect(block.Definition.SwitchProtectionBlockIds).Any());
        if (movement.Request is not null)
        {
            return $"{prefix}active movement from block {movement.Request!.TargetBlockId} uses its protection zone.";
        }

        return IsApproachLocked(block)
            ? $"{prefix}approach locked for {SwitchApproachLockSeconds:0} s after an authority over its protection zone was withdrawn while its train may still be approaching."
            : null;
    }

    private bool CrossingRequired(Block block) => block.Definition.CrossingProtectionBlockIds.Any(id =>
        {
            var zone = GetBlock(id);
            return !IsFresh(zone) || zone.Feedback is not { Occupancy: OccupancyState.Clear, IsFaulted: false, IsClosed: false };
        }) || _requests.Values.Any(request => IsActive(request) && GetRequestPath(request).Contains(block.Definition.BlockId));
}
