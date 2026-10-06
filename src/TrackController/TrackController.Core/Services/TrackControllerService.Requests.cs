using TrackController.Contracts.Messages;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Core.Services;

/// <summary>Evaluates track conditions and rejects movement or equipment commands that violate protection rules.</summary>
public sealed partial class TrackControllerService
{
    /// <summary>Validates the request origin and route before evaluating conservative movement limits.</summary>
    public void SubmitMovement(WaysideMovementRequestMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            var target = GetOwnedBlock(message.TargetBlockId);
            if (string.IsNullOrWhiteSpace(message.RequestId) || message.RequestId.Length > 128 || message.TrainId is null || message.TrainId.Length > 128)
                throw new ArgumentException("Provide a request ID (up to 128 characters). The train label is optional.");
            if (!message.IsActive)
            {
                if (_requests.TryGetValue(message.TargetBlockId, out var current) && current.Value.RequestId != message.RequestId)
                    throw new ArgumentException("Cancellation must identify the current request ID for this target block.");
                _requests.Remove(message.TargetBlockId);
                Reevaluate();
                return;
            }

            if (_retiredRequests.Contains(message.RequestId))
                throw new ArgumentException("This request's target block cleared. Submit a new request ID and current front position.");
            if (message.FrontOffsetMeters is not double offset || !double.IsFinite(offset) || offset < 0 || offset >= target.Definition.LengthMeters || !double.IsFinite(message.SuggestedSpeedMetersPerSecond) || message.SuggestedSpeedMetersPerSecond < 0 || !double.IsFinite(message.SuggestedAuthorityMeters) || message.SuggestedAuthorityMeters < 0 || message.PathBlockIds is null || message.PathBlockIds.Count > _blocks.Count)
                throw new ArgumentException("Specify a front offset within the target block and finite, nonnegative speed/authority.");
            var path = new[]
            {
                message.TargetBlockId
            }.Concat(message.PathBlockIds).ToArray();
            if (message.EntryBlockId is not null && !GetBlock(message.EntryBlockId).Definition.NextBlockIds.Contains(message.TargetBlockId))
                throw new ArgumentException("The entry block must connect toward the target block.");
            if (path.Distinct().Count() != path.Length)
                throw new ArgumentException("A request cannot revisit a block.");
            for (int i = 0; i < path.Length; i++)
            {
                var block = GetOwnedBlock(path[i]);
                if (i + 1 < path.Length && !block.Definition.NextBlockIds.Contains(path[i + 1]))
                    throw new ArgumentException($"No permitted connection from {path[i]} to {path[i + 1]}.");
            }

            if (_requestOrigins.TryGetValue(message.RequestId, out var previous) && !previous.Matches(message))
                throw new ArgumentException("A request ID cannot change its authority origin or route. Use a new ID after movement.");
            if (!_requestOrigins.ContainsKey(message.RequestId) && _requestOrigins.Count >= 10000)
                throw new InvalidOperationException("Request ID history is full. Restart the controller while the simulation is stopped.");
            _requestOrigins[message.RequestId] = new RequestOrigin(message.TargetBlockId, message.EntryBlockId, offset, [.. message.PathBlockIds]);
            _requests[message.TargetBlockId] = new Request(new WaysideMovementRequestMessage { RequestId = message.RequestId, TrainId = message.TrainId, TargetBlockId = message.TargetBlockId, EntryBlockId = message.EntryBlockId, FrontOffsetMeters = offset, PathBlockIds = [.. message.PathBlockIds], SuggestedAuthorityMeters = message.SuggestedAuthorityMeters, SuggestedSpeedMetersPerSecond = message.SuggestedSpeedMetersPerSecond }, _clock.GetUtcNow());
            Reevaluate();
        }
    }

    /// <summary>Accepts a switch request only when its complete protection zone permits movement.</summary>
    public string RequestSwitch(SwitchPositionRequestMessage message)
    {
        lock (_gate)
        {
            var block = GetOwnedBlock(message.BlockId);
            if (block.Definition.NormalNextBlockId is null || message.RequestedPosition is not (SwitchPosition.Normal or SwitchPosition.Reverse))
                throw new ArgumentException("Select an owned switch and Normal or Reverse.");
            if (!CanMoveSwitch(block))
                throw new InvalidOperationException("Switch protected: protection blocks must be fresh, clear, fault-free, explicitly unreserved, and outside active authority.");
            block.DesiredSwitch = message.RequestedPosition;
            Reevaluate();
            return "Switch request accepted. Authority waits for matching feedback.";
        }
    }

    /// <summary>Closes a clear block immediately or holds an occupied block pending safe clearance.</summary>
    public string RequestMaintenance(MaintenanceRequestMessage message)
    {
        lock (_gate)
        {
            var block = GetOwnedBlock(message.BlockId);
            if (!Enum.IsDefined(message.RequestedState))
                throw new ArgumentException("Invalid maintenance state.");
            block.Maintenance = message.RequestedState == MaintenanceState.Open ? "Open" : IsFresh(block) && block.Feedback?.Occupancy == OccupancyState.Clear ? "Closed" : "Pending closure";
            Reevaluate();
            return $"Block {message.BlockId}: {block.Maintenance}.";
        }
    }

    /// <summary>Applies maintenance commands only when the corresponding equipment interlocks permit them.</summary>
    public void ApplyManual(WaysideManualCommandMessage message)
    {
        lock (_gate)
        {
            if (!_maintenanceMode)
                throw new InvalidOperationException("Enable maintenance mode before sending manual commands.");
            var block = GetOwnedBlock(message.BlockId);
            if (message.Switch is not null && (block.Definition.NormalNextBlockId is null || message.Switch is not (SwitchPosition.Normal or SwitchPosition.Reverse) || !CanMoveSwitch(block)))
                throw new InvalidOperationException("Manual switch movement is blocked by the switch protection zone.");
            if (message.Signal is not null && (!block.Definition.HasSignal || message.Signal != SignalState.Red))
                throw new InvalidOperationException("Manual signals may be set to Red. Proceed aspects require automatic route protection.");
            if (message.Crossing is not null && (!block.Definition.HasCrossing || message.Crossing is not (CrossingState.Open or CrossingState.Closed) || (message.Crossing == CrossingState.Open && CrossingRequired(block))))
                throw new InvalidOperationException("Crossing cannot open while its zone is occupied, unavailable, faulted, or requested.");
            if (message.Switch is { } position)
                block.DesiredSwitch = position;
            if (message.Signal is not null)
                block.ManualRed = true;
            if (message.Crossing is { } crossing)
                block.ManualCrossingClosed = crossing == CrossingState.Closed;
            Reevaluate();
        }
    }

    private bool CanMoveSwitch(Block block) => block.Definition.SwitchProtectionBlockIds.All(id =>
    {
        var zone = GetBlock(id);
        return IsFresh(zone) && zone.Feedback is { Occupancy: OccupancyState.Clear, IsFaulted: false, IsReservedByCtc: false };
    }) && !(_snapshot?.Authorizations.Any(authorization => authorization.AuthorizedAuthorityMeters > 0 && new[] { authorization.TargetBlockId }.Concat(authorization.PathBlockIds).Intersect(block.Definition.SwitchProtectionBlockIds).Any()) ?? false);
    private bool CrossingRequired(Block block) => block.Definition.CrossingProtectionBlockIds.Any(id =>
    {
        var zone = GetBlock(id);
        return !IsFresh(zone) || zone.Feedback is not { Occupancy: OccupancyState.Clear, IsFaulted: false };
    }) || _requests.Values.Any(request => IsActive(request) && GetRequestPath(request).Contains(block.Definition.BlockId));
}
