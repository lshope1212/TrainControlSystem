using MovementResult = (TrainControl.Contracts.Messages.MovementSuggestionMessage Request, double AuthorizedSpeedMetersPerSecond, double AuthorizedAuthorityMeters, string Status, string Reason);
using TrainControl.Contracts.Messages;
using TrackController.Contracts.Messages;
using TrainControl.Contracts.Enums;

namespace TrackController.Core.Services;

/// <summary>Evaluates track conditions and rejects movement or equipment commands that violate protection rules.</summary>
public sealed partial class TrackControllerService
{
    private void Reevaluate()
    {
        var now = _clock.GetUtcNow();
        if (!_paused)
        {
            _simulationSeconds += Math.Max(0, (now - _lastPulse).TotalSeconds) * _multiplier;
        }

        _lastPulse = now;
        foreach (var block in _blocks.Values.Where(block => _owned.Contains(block.Definition.BlockId)))
        {
            block.CommandedSignal = SignalState.Red;
            if (block.Definition.HasCrossing)
            {
                bool required = CrossingRequired(block) || block.ManualCrossingClosed;
                bool plcClose = _hardwareMode;
                if (_hardwareMode && _plc is not null)
                {
                    var inputs = PlcInputs();
                    inputs["active_route"] = [_requests.Values.Any(request => IsActive(request) && GetRequestPath(request).Contains(block.Definition.BlockId))];
                    inputs["manual_crossing_close"] = [block.ManualCrossingClosed];
                    IReadOnlyDictionary<string, bool[]>? outputs = null;
                    foreach (string id in block.Definition.CrossingProtectionBlockIds)
                    {
                        SetBlockInputs(inputs, GetBlock(id));
                        outputs = _plc.Execute(inputs);
                        inputs["crossing_needed_in"] = outputs["crossing_needed"];
                    }
                    plcClose = (outputs ?? _plc.Execute(inputs))["crossing_close"][0];
                }
                block.CommandedCrossing = required || plcClose ? CrossingState.Closed : CrossingState.Open;
            }

            if (block.Definition.NormalNextBlockId is not null)
            {
                var reported = IsFresh(block) ? block.Feedback!.Switch : SwitchPosition.Unknown;
                var desired = block.DesiredSwitch == SwitchPosition.Unknown ? reported : block.DesiredSwitch;
                bool safe = GetSwitchProtectionReason(block) is null;
                bool plcMove = !_hardwareMode;
                if (_hardwareMode && _plc is not null)
                {
                    var outputs = SwitchPlc(block, desired);
                    desired = outputs["switch_normal"][0] ? SwitchPosition.Normal : SwitchPosition.Reverse;
                    plcMove = outputs["switch_move"][0];
                }

                block.CommandedSwitch = plcMove && safe && (!_paused || _maintenanceMode) ? desired : reported;
            }
        }

        var candidates = _requests.Values.Select(request => _hardwareMode ? EvaluateVital(request) : EvaluateSoftware(request)).ToList();
        foreach (var authorization in candidates.Where(authorization => !_hardwareMode && authorization.AuthorizedAuthorityMeters > 0 && GetCoveredBlocks(authorization).All(id => !GetBlock(id).Definition.HasCrossing || GetBlock(id).Feedback?.Crossing == CrossingState.Closed)))
        {
            foreach (string id in GetCoveredBlocks(authorization))
            {
                var block = GetBlock(id);
                if (block.Definition.HasSignal && !block.ManualRed)
                {
                    bool restricted = authorization.AuthorizedAuthorityMeters < authorization.Request.SuggestedAuthorityMeters;
                    block.CommandedSignal = restricted ? SignalState.Yellow : SignalState.Green;
                }
            }
        }

        var authorizations = new List<MovementResult>();
        foreach (var authorization in candidates)
        {
            var result = authorization;
            foreach (string id in _hardwareMode ? Enumerable.Empty<string>() : GetCoveredBlocks(authorization))
            {
                var block = GetBlock(id);
                if (block.Definition.HasSignal && (block.CommandedSignal == SignalState.Yellow || block.Feedback?.Signal == SignalState.Yellow))
                {
                    result = result with
                    {
                        AuthorizedSpeedMetersPerSecond = Math.Min(result.AuthorizedSpeedMetersPerSecond, 5),
                        Status = result.AuthorizedAuthorityMeters > 0 ? "Restricted" : "Withheld"
                    };
                }

                if (block.Definition.HasSignal && (block.ManualRed || block.Feedback?.Signal is not (SignalState.Green or SignalState.SuperGreen or SignalState.Yellow)))
                {
                    result = Stop(result, $"Waiting for proceed signal feedback at block {id}.");
                }

                if (block.Definition.HasCrossing && block.Feedback?.Crossing != CrossingState.Closed)
                {
                    result = Stop(result, $"Waiting for crossing {id} to report closed.");
                }
            }

            if (!IsAuthorizationSafe(result))
            {
                result = Stop(result, "Independent safety check withheld authority.");
            }

            authorizations.Add(result);
        }

        // Rejected PLC candidates cannot leave a proceed aspect behind.
        var permittedBlocks = authorizations.Where(authorization => authorization.AuthorizedAuthorityMeters > 0)
            .SelectMany(GetCoveredBlocks).ToHashSet(StringComparer.Ordinal);
        foreach (var block in _blocks.Values.Where(block => _owned.Contains(block.Definition.BlockId) && block.Definition.HasSignal))
        {
            if (!permittedBlocks.Contains(block.Definition.BlockId))
            {
                block.CommandedSignal = SignalState.Red;
            }
        }
        var resultsByBlock = authorizations.ToDictionary(result => result.Request.TargetBlockId);
        var blockStatuses = _blocks.Values.Where(block => _owned.Contains(block.Definition.BlockId) || _neighbors.Contains(block.Definition.BlockId)).Select(block =>
        {
            var definition = block.Definition;
            var feedback = block.Feedback;
            bool fresh = IsFresh(block);
            resultsByBlock.TryGetValue(definition.BlockId, out var movement);
            var request = movement.Request;
            return new WaysideBlockStatus(definition.BlockId, definition.LengthMeters, definition.SpeedLimitMetersPerSecond,
                definition.NextBlockIds.AsReadOnly(), definition.MapX, definition.MapY, definition.NormalNextBlockId is not null,
                definition.HasSignal, definition.HasCrossing, feedback?.Occupancy ?? OccupancyState.Unknown,
                feedback?.IsClosed == true ? "Closed" : block.Maintenance, fresh, feedback?.IsFaulted ?? false, fresh ? feedback?.IsReservedByCtc : null,
                block.CommandedSwitch, feedback?.Switch ?? SwitchPosition.Unknown, block.CommandedSignal,
                feedback?.Signal ?? SignalState.Unknown, block.CommandedCrossing, feedback?.Crossing ?? CrossingState.Unknown,
                fresh ? feedback?.ActualSpeedMetersPerSecond : null,
                !fresh ? "Feedback unavailable" : feedback!.IsFaulted ? "Fault reported" : feedback.IsClosed ? "Closed" : block.Maintenance != "Open" ? block.Maintenance : "Current",
                definition.StationName)
            {
                Request = request is null ? null : new MovementSuggestionMessage
                {
                    RequestId = request.RequestId, TrainId = request.TrainId, TargetBlockId = request.TargetBlockId,
                    EntryBlockId = request.EntryBlockId, PathBlockIds = [.. request.PathBlockIds], AuthorityReference = request.AuthorityReference,
                    SuggestedSpeedMetersPerSecond = request.SuggestedSpeedMetersPerSecond, SuggestedAuthorityMeters = request.SuggestedAuthorityMeters
                },
                AuthorizedSpeedMetersPerSecond = movement.AuthorizedSpeedMetersPerSecond,
                AuthorizedAuthorityMeters = movement.AuthorizedAuthorityMeters,
                MovementStatus = movement.Status ?? "", MovementReason = movement.Reason ?? ""
            };
        }).ToArray();

        var validUntil = now.AddSeconds(1.5);
        foreach (var authorization in authorizations.Where(authorization => authorization.AuthorizedAuthorityMeters > 0))
        {
            var requestExpiry = _requests[authorization.Request.TargetBlockId].Received.AddSeconds(_configuration.RequestTimeoutSeconds);
            if (requestExpiry < validUntil)
            {
                validUntil = requestExpiry;
            }

            foreach (string id in GetCoveredBlocks(authorization))
            {
                var block = GetBlock(id);
                var measuredExpiry = block.Feedback!.ObservedAtUtc.AddSeconds(_configuration.FeedbackTimeoutSeconds);
                var receivedExpiry = block.Received.AddSeconds(_configuration.FeedbackTimeoutSeconds);
                if (measuredExpiry < validUntil)
                {
                    validUntil = measuredExpiry;
                }

                if (receivedExpiry < validUntil)
                {
                    validUntil = receivedExpiry;
                }
            }
        }

        _snapshot = new WaysideSnapshotMessage(_configuration.WaysideId, _configuration.LineName, now, validUntil, _simulationSeconds, _paused, _multiplier, _hardwareMode, _maintenanceMode, _plcName, _plcStatus, Array.AsReadOnly(blockStatuses.Where(block => _owned.Contains(block.BlockId)).ToArray()))
        {
            NeighborObservations = blockStatuses.Where(block => _neighbors.Contains(block.BlockId)).Select(block => block with
            {
                CommandedSwitch = SwitchPosition.Unknown, CommandedSignal = SignalState.Unknown, CommandedCrossing = CrossingState.Unknown,
                Occupancy = block.IsFresh ? block.Occupancy : OccupancyState.Unknown,
                ReportedSignal = block.IsFresh ? block.ReportedSignal : SignalState.Unknown,
                ReportedSwitch = block.IsFresh ? block.ReportedSwitch : SwitchPosition.Unknown,
                ReportedCrossing = block.IsFresh ? block.ReportedCrossing : CrossingState.Unknown
            }).ToArray()
        };
    }

    /// <summary>Standalone no-PLC comparison mode only; routed PC/Pi territories use EvaluateVital.</summary>
    private MovementResult EvaluateSoftware((MovementSuggestionMessage Value, DateTimeOffset Received) receivedRequest)
    {
        var request = receivedRequest.Value;
        MovementResult output = (request, 0, 0, "Withheld", "");
        if (_paused)
        {
            return Stop(output, "Simulation paused.");
        }

        if (_maintenanceMode)
        {
            return Stop(output, "Maintenance mode is active.");
        }

        if (!IsActive(receivedRequest))
        {
            return Stop(output, "CTC request expired; refresh it or submit a new target block and route.");
        }

        if (_retiredRequests.Contains(request.RequestId))
        {
            return Stop(output, "Target block cleared; a new request and current target block are required.");
        }

        if (GetBlock(request.TargetBlockId).Feedback?.Occupancy != OccupancyState.Occupied)
        {
            return Stop(output, "Target block is not occupied. Submit a new origin after movement.");
        }

        var path = GetRequestPath(receivedRequest).ToArray();
        if (_requests.Values.Any(other => other != receivedRequest && IsActive(other) && GetRequestPath(other).Intersect(path).Any()))
        {
            return Stop(output, "Conflicting active movement requests overlap this route.");
        }

        double distance = 0, speed = request.SuggestedSpeedMetersPerSecond;
        string reason = "Requested route protected.";
        for (int i = 0; i < path.Length; i++)
        {
            var block = GetBlock(path[i]);
            if (!IsFresh(block) || block.Feedback!.Occupancy == OccupancyState.Unknown)
            {
                return Stop(output, $"Block {path[i]} feedback is missing, stale, or unknown.");
            }

            if (block.Feedback.IsFaulted)
            {
                return Stop(output, $"Block {path[i]} reports a fault.");
            }

            if (block.Definition.NormalNextBlockId is not null)
            {
                if (block.CommandedSwitch != block.Feedback.Switch || block.Feedback.Switch == SwitchPosition.Unknown)
                {
                    return Stop(output, $"Waiting for switch {path[i]} alignment feedback.");
                }

                if (i + 1 < path.Length)
                {
                    string? connected = block.Feedback.Switch == SwitchPosition.Normal ? block.Definition.NormalNextBlockId : block.Definition.ReverseNextBlockId;
                    string? previous = i > 0 ? path[i - 1] : request.EntryBlockId;
                    bool aligned = block.Definition.StemBlockId is { } stem ? (previous == stem && path[i + 1] == connected) || (previous == connected && path[i + 1] == stem) : connected == path[i + 1];
                    if (!aligned)
                    {
                        return Stop(output, $"Switch {path[i]} connects to {connected}; this request needs {path[i + 1]} (verify entry and alignment).");
                    }
                }
            }

            if (block.Feedback.IsClosed || block.Maintenance == "Closed" || (i > 0 && block.Maintenance != "Open") || (i > 0 && block.Feedback.Occupancy != OccupancyState.Clear))
            {
                reason = $"Authority ends before block {path[i]} ({(block.Maintenance != "Open" ? block.Maintenance : "occupied")}).";
                break;
            }

            // This locates a fixed route endpoint from a known block boundary, not from an inferred train position.
            distance += block.Definition.LengthMeters;
            speed = Math.Min(speed, block.Definition.SpeedLimitMetersPerSecond);
            if (block.Definition.HasSignal && block.Feedback.Signal == SignalState.Yellow)
            {
                speed = Math.Min(speed, 5);
            }

            if (distance >= request.SuggestedAuthorityMeters)
            {
                break;
            }
        }

        distance = Math.Min(distance, request.SuggestedAuthorityMeters);
        if (distance <= 0 || speed <= 0)
        {
            return Stop(output, "Zero speed or no available authority.");
        }

        return output with
        {
            AuthorizedAuthorityMeters = distance,
            AuthorizedSpeedMetersPerSecond = speed,
            Status = distance < request.SuggestedAuthorityMeters || speed < request.SuggestedSpeedMetersPerSecond ? "Restricted" : "Approved",
            Reason = reason
        };
    }

    /// <summary>Checks movement limits and current conditions for a previously validated request route.</summary>
    private bool IsAuthorizationSafe(MovementResult output)
    {
        if (!double.IsFinite(output.AuthorizedAuthorityMeters) || !double.IsFinite(output.AuthorizedSpeedMetersPerSecond) || output.AuthorizedAuthorityMeters < 0 || output.AuthorizedSpeedMetersPerSecond < 0)
        {
            return false;
        }

        if (output.AuthorizedAuthorityMeters == 0)
        {
            return output.AuthorizedSpeedMetersPerSecond == 0;
        }

        if (output.AuthorizedSpeedMetersPerSecond <= 0 || output.AuthorizedAuthorityMeters > output.Request.SuggestedAuthorityMeters || output.AuthorizedSpeedMetersPerSecond > output.Request.SuggestedSpeedMetersPerSecond)
        {
            return false;
        }

        var sequence = new[] { output.Request.TargetBlockId }.Concat(output.Request.PathBlockIds).ToArray();
        double remaining = output.AuthorizedAuthorityMeters;
        for (int i = 0; i < sequence.Length && (i == 0 || remaining > 0.000001); i++)
        {
            var block = _blocks[sequence[i]];
            if (!IsFresh(block))
            {
                return false;
            }

            var definition = block.Definition;
            var feedback = block.Feedback!;
            if (feedback.IsFaulted || feedback.IsClosed || (i == 0 ? feedback.Occupancy != OccupancyState.Occupied : feedback.Occupancy != OccupancyState.Clear) || block.Maintenance == "Closed" || (i > 0 && block.Maintenance != "Open") || output.AuthorizedSpeedMetersPerSecond > definition.SpeedLimitMetersPerSecond)
            {
                return false;
            }

            if (definition.HasCrossing && (block.CommandedCrossing != CrossingState.Closed || feedback.Crossing != CrossingState.Closed))
            {
                return false;
            }

            if (definition.HasSignal && (block.CommandedSignal is not (SignalState.Green or SignalState.SuperGreen or SignalState.Yellow) || feedback.Signal is not (SignalState.Green or SignalState.SuperGreen or SignalState.Yellow)))
            {
                return false;
            }

            if (definition.HasSignal && (block.CommandedSignal == SignalState.Yellow || feedback.Signal == SignalState.Yellow) && output.AuthorizedSpeedMetersPerSecond > 5)
            {
                return false;
            }

            remaining -= definition.LengthMeters;
            if (remaining > 0.000001 && i + 1 >= sequence.Length)
            {
                return false;
            }

            if (definition.NormalNextBlockId is not null)
            {
                if (block.CommandedSwitch == SwitchPosition.Unknown || block.CommandedSwitch != feedback.Switch)
                {
                    return false;
                }

                var exit = feedback.Switch == SwitchPosition.Normal ? definition.NormalNextBlockId : definition.ReverseNextBlockId;
                if (remaining > 0.000001)
                {
                    string? entry = i > 0 ? sequence[i - 1] : output.Request.EntryBlockId;
                    if (definition.StemBlockId is { } stem)
                    {
                        if (!((entry == stem && sequence[i + 1] == exit) || (entry == exit && sequence[i + 1] == stem)))
                        {
                            return false;
                        }
                    }
                    else if (sequence[i + 1] != exit)
                    {
                        return false;
                    }
                }
            }
        }

        return remaining <= 0.000001;
    }

    private IEnumerable<string> GetCoveredBlocks(MovementResult authorization)
    {
        double remaining = authorization.AuthorizedAuthorityMeters;
        bool first = true;
        foreach (string id in new[]
        {
            authorization.Request.TargetBlockId
        }.Concat(authorization.Request.PathBlockIds))
        {
            if (remaining <= 0 || (!first && remaining <= 0.000001))
            {
                yield break;
            }

            first = false;
            yield return id;
            remaining -= GetBlock(id).Definition.LengthMeters;
        }
    }

    private static MovementResult Stop(MovementResult authorization, string reason) => authorization with
    {
        AuthorizedAuthorityMeters = 0,
        AuthorizedSpeedMetersPerSecond = 0,
        Status = "Withheld",
        Reason = reason
    };

}
