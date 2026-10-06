using TrackController.Contracts.Messages;
using TrackController.Core.Safety;
using TrainControl.Contracts.Enums;

namespace TrackController.Core.Services;

/// <summary>Evaluates track conditions and rejects movement or equipment commands that violate protection rules.</summary>
public sealed partial class TrackControllerService
{
    private IReadOnlyDictionary<string, bool> EvaluatePlc(bool routeSafe, bool switchSafe = false, bool requestedNormal = true, bool crossingRequired = false)
    {
        var inputs = new Dictionary<string, bool>
        {
            ["route_safe"] = routeSafe,
            ["switch_safe"] = switchSafe,
            ["requested_normal"] = requestedNormal,
            ["crossing_required"] = crossingRequired,
            ["running"] = !_paused && !_maintenanceMode
        };
        return _plc!.Execute(inputs);
    }

    private void Reevaluate()
    {
        var now = _clock.GetUtcNow();
        if (!_paused)
            _simulationSeconds += Math.Max(0, (now - _lastPulse).TotalSeconds) * _multiplier;
        _lastPulse = now;
        foreach (var block in _blocks.Values.Where(block => _owned.Contains(block.Definition.BlockId)))
        {
            block.CommandedSignal = SignalState.Red;
            if (block.Definition.HasCrossing)
            {
                bool required = CrossingRequired(block) || block.ManualCrossingClosed;
                bool plcClose = _hardwareMode && (_plc is null || EvaluatePlc(false, crossingRequired: required)["crossing_close"]);
                block.CommandedCrossing = required || plcClose ? CrossingState.Closed : CrossingState.Open;
            }

            if (block.Definition.NormalNextBlockId is not null)
            {
                var reported = IsFresh(block) ? block.Feedback!.Switch : SwitchPosition.Unknown;
                var desired = block.DesiredSwitch == SwitchPosition.Unknown ? reported : block.DesiredSwitch;
                bool safe = CanMoveSwitch(block);
                if (_hardwareMode && _plc is not null)
                    desired = EvaluatePlc(false, safe, desired == SwitchPosition.Normal)["switch_normal"] ? SwitchPosition.Normal : SwitchPosition.Reverse;
                block.CommandedSwitch = safe && (!_paused || _maintenanceMode) && (!_hardwareMode || _plc is not null) ? desired : reported;
            }
        }

        var candidates = _requests.Values.Select(Evaluate).ToList();
        foreach (var authorization in candidates.Where(authorization => authorization.AuthorizedAuthorityMeters > 0 && GetCoveredBlocks(authorization).All(id => !GetBlock(id).Definition.HasCrossing || GetBlock(id).Feedback?.Crossing == CrossingState.Closed)))
            foreach (string id in GetCoveredBlocks(authorization))
            {
                var block = GetBlock(id);
                if (block.Definition.HasSignal && !block.ManualRed)
                    block.CommandedSignal = authorization.AuthorizedAuthorityMeters < authorization.SuggestedAuthorityMeters ? SignalState.Yellow : SignalState.Green;
            }

        var blockStatuses = _blocks.Values.Where(block => _owned.Contains(block.Definition.BlockId)).Select(ToStatus).ToArray();
        var authorizations = new List<WaysideAuthorization>();
        foreach (var authorization in candidates)
        {
            var result = authorization;
            foreach (string id in GetCoveredBlocks(authorization))
            {
                var block = GetBlock(id);
                if (block.Definition.HasSignal && (block.CommandedSignal == SignalState.Yellow || block.Feedback?.Signal == SignalState.Yellow))
                    result = result with
                    {
                        AuthorizedSpeedMetersPerSecond = Math.Min(result.AuthorizedSpeedMetersPerSecond, 5),
                        Status = result.AuthorizedAuthorityMeters > 0 ? "Restricted" : "Withheld"
                    };
                if (block.Definition.HasSignal && (block.ManualRed || block.Feedback?.Signal is not (SignalState.Green or SignalState.Yellow)))
                    result = Stop(result, $"Waiting for proceed signal feedback at block {id}.");
                if (block.Definition.HasCrossing && block.Feedback?.Crossing != CrossingState.Closed)
                    result = Stop(result, $"Waiting for crossing {id} to report closed.");
            }

            if (!AuthorizationGuard.IsSafe(result, blockStatuses, _configuration))
                result = Stop(result, "Independent safety check withheld authority.");
            authorizations.Add(result);
        }

        var validUntil = now.AddSeconds(1.5);
        foreach (var authorization in authorizations.Where(authorization => authorization.AuthorizedAuthorityMeters > 0))
        {
            var requestExpiry = _requests[authorization.TargetBlockId].Received.AddSeconds(_configuration.RequestTimeoutSeconds);
            if (requestExpiry < validUntil)
                validUntil = requestExpiry;
            foreach (string id in GetCoveredBlocks(authorization))
            {
                var block = GetBlock(id);
                var measuredExpiry = block.Feedback!.ObservedAtUtc.AddSeconds(_configuration.FeedbackTimeoutSeconds);
                var receivedExpiry = block.Received.AddSeconds(_configuration.FeedbackTimeoutSeconds);
                if (measuredExpiry < validUntil)
                    validUntil = measuredExpiry;
                if (receivedExpiry < validUntil)
                    validUntil = receivedExpiry;
            }
        }

        _snapshot = new WaysideSnapshotMessage(_configuration.WaysideId, _configuration.LineName, now, validUntil, _simulationSeconds, _paused, _multiplier, _hardwareMode, _maintenanceMode, _plcName, _plcStatus, Array.AsReadOnly(blockStatuses), authorizations.AsReadOnly())
        {
            NeighborObservations = _neighbors.Select(id =>
            {
                var block = GetBlock(id);
                bool fresh = IsFresh(block);
                var feedback = block.Feedback;
                return new WaysideNeighborObservation(id, fresh, fresh ? feedback!.Occupancy : OccupancyState.Unknown, feedback?.IsFaulted ?? false, fresh ? feedback!.Signal : SignalState.Unknown, fresh ? feedback!.Switch : SwitchPosition.Unknown, fresh ? feedback!.Crossing : CrossingState.Unknown, fresh ? feedback!.IsReservedByCtc : null);
            }).ToArray()
        };
    }

    private WaysideAuthorization Evaluate(Request receivedRequest)
    {
        var request = receivedRequest.Value;
        var output = new WaysideAuthorization(request.RequestId, request.TrainId, request.TargetBlockId, request.EntryBlockId, request.FrontOffsetMeters!.Value, request.PathBlockIds.AsReadOnly(), request.SuggestedSpeedMetersPerSecond, request.SuggestedAuthorityMeters, 0, 0, "Withheld", "");
        if (_paused)
            return Stop(output, "Simulation paused.");
        if (_maintenanceMode)
            return Stop(output, "Maintenance mode is active.");
        if (_hardwareMode && _plc is null)
            return Stop(output, "No valid PLC program.");
        if (!IsActive(receivedRequest))
            return Stop(output, "CTC request expired; refresh its origin or submit a new position.");
        if (_retiredRequests.Contains(request.RequestId))
            return Stop(output, "Target block cleared; a new request and position are required.");
        if (GetBlock(request.TargetBlockId).Feedback?.Occupancy != OccupancyState.Occupied)
            return Stop(output, "Target block is not occupied. Submit a new origin after movement.");
        var path = GetRequestPath(receivedRequest).ToArray();
        if (_requests.Values.Any(other => other != receivedRequest && IsActive(other) && GetRequestPath(other).Intersect(path).Any()))
            return Stop(output, "Conflicting active movement requests overlap this route.");
        double distance = 0, speed = request.SuggestedSpeedMetersPerSecond;
        string reason = "Requested route protected.";
        for (int i = 0; i < path.Length; i++)
        {
            var block = GetBlock(path[i]);
            if (!IsFresh(block) || block.Feedback!.Occupancy == OccupancyState.Unknown)
                return Stop(output, $"Block {path[i]} feedback is missing, stale, or unknown.");
            if (block.Feedback.IsFaulted)
                return Stop(output, $"Block {path[i]} reports a fault.");
            if (block.Definition.NormalNextBlockId is not null)
            {
                if (block.CommandedSwitch != block.Feedback.Switch || block.Feedback.Switch == SwitchPosition.Unknown)
                    return Stop(output, $"Waiting for switch {path[i]} alignment feedback.");
                if (i + 1 < path.Length)
                {
                    string? connected = block.Feedback.Switch == SwitchPosition.Normal ? block.Definition.NormalNextBlockId : block.Definition.ReverseNextBlockId;
                    string? previous = i > 0 ? path[i - 1] : request.EntryBlockId;
                    bool aligned = block.Definition.StemBlockId is { } stem ? (previous == stem && path[i + 1] == connected) || (previous == connected && path[i + 1] == stem) : connected == path[i + 1];
                    if (!aligned)
                        return Stop(output, $"Switch {path[i]} connects to {connected}; this request needs {path[i + 1]} (verify entry and alignment).");
                }
            }

            if (block.Maintenance == "Closed" || (i > 0 && block.Maintenance != "Open") || (i > 0 && block.Feedback.Occupancy != OccupancyState.Clear))
            {
                reason = $"Authority ends before block {path[i]} ({(block.Maintenance != "Open" ? block.Maintenance : "occupied")}).";
                break;
            }

            distance += block.Definition.LengthMeters - (i == 0 ? request.FrontOffsetMeters.Value : 0);
            speed = Math.Min(speed, block.Definition.SpeedLimitMetersPerSecond);
            if (block.Definition.HasSignal && block.Feedback.Signal == SignalState.Yellow)
                speed = Math.Min(speed, 5);
            if (distance >= request.SuggestedAuthorityMeters)
                break;
        }

        distance = Math.Min(distance, request.SuggestedAuthorityMeters);
        if (distance <= 0 || speed <= 0)
            return Stop(output, "Zero speed or no available authority.");
        if (_hardwareMode)
        {
            var values = EvaluatePlc(true);
            if (!values["allow_route"] || !values["signal_clear"])
                return Stop(output, "PLC withholds movement permission.");
        }

        return output with
        {
            AuthorizedAuthorityMeters = distance,
            AuthorizedSpeedMetersPerSecond = speed,
            Status = distance < request.SuggestedAuthorityMeters || speed < request.SuggestedSpeedMetersPerSecond ? "Restricted" : "Approved",
            Reason = reason
        };
    }

    private IEnumerable<string> GetCoveredBlocks(WaysideAuthorization authorization)
    {
        double remaining = authorization.AuthorizedAuthorityMeters;
        foreach (string id in new[]
        {
            authorization.TargetBlockId
        }.Concat(authorization.PathBlockIds))
        {
            if (remaining <= 0.000001)
                yield break;
            yield return id;
            remaining -= GetBlock(id).Definition.LengthMeters - (id == authorization.TargetBlockId ? authorization.FrontOffsetMeters : 0);
        }
    }

    private static WaysideAuthorization Stop(WaysideAuthorization authorization, string reason) => authorization with
    {
        AuthorizedAuthorityMeters = 0,
        AuthorizedSpeedMetersPerSecond = 0,
        Status = "Withheld",
        Reason = reason
    };
    private WaysideBlockStatus ToStatus(Block block)
    {
        var blockDefinition = block.Definition;
        var feedback = block.Feedback;
        bool fresh = IsFresh(block);
        return new WaysideBlockStatus(blockDefinition.BlockId, blockDefinition.LengthMeters, blockDefinition.SpeedLimitMetersPerSecond, blockDefinition.NextBlockIds.AsReadOnly(), blockDefinition.MapX, blockDefinition.MapY, blockDefinition.NormalNextBlockId is not null, blockDefinition.HasSignal, blockDefinition.HasCrossing, feedback?.Occupancy ?? OccupancyState.Unknown, block.Maintenance, fresh, feedback?.IsFaulted ?? false, fresh ? feedback?.IsReservedByCtc : null, block.CommandedSwitch, feedback?.Switch ?? SwitchPosition.Unknown, block.CommandedSignal, feedback?.Signal ?? SignalState.Unknown, block.CommandedCrossing, feedback?.Crossing ?? CrossingState.Unknown, fresh ? feedback?.ActualSpeedMetersPerSecond : null, !fresh ? "Feedback unavailable" : feedback!.IsFaulted ? "Fault reported" : block.Maintenance != "Open" ? block.Maintenance : "Current");
    }
}
