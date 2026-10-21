using TrackController.Contracts.Messages;
using TrackController.Core.Control;
using TrainControl.Contracts.Enums;

namespace TrackController.Core.Services;

/// <summary>Marshals raw facts and bit words to the PLC; the program owns normal control decisions.</summary>
public sealed partial class TrackControllerService
{
    private Dictionary<string, bool[]> PlcInputs()
    {
        var inputs = VitalPlcProgram.BooleanInputs.ToDictionary(name => name, _ => new[] { false }, StringComparer.Ordinal);
        foreach (string name in VitalPlcProgram.WordInputs)
        {
            inputs[name] = new bool[PlcBits.Width];
        }
        inputs["running"] = [!_paused];
        inputs["maintenance"] = [_maintenanceMode];
        return inputs;
    }

    private void SetBlockInputs(Dictionary<string, bool[]> inputs, Block block)
    {
        var feedback = block.Feedback;
        inputs["feedback_fresh"] = [IsFresh(block)];
        inputs["block_clear"] = [feedback?.Occupancy == OccupancyState.Clear];
        inputs["block_unknown"] = [feedback is null || feedback.Occupancy == OccupancyState.Unknown];
        inputs["block_faulted"] = [feedback?.IsFaulted ?? false];
        inputs["block_closed"] = [block.Maintenance == "Closed"];
        inputs["block_pending"] = [block.Maintenance == "Pending closure"];
        inputs["has_signal"] = [block.Definition.HasSignal];
        inputs["signal_proceed"] = [feedback?.Signal is SignalState.Green or SignalState.SuperGreen or SignalState.Yellow];
        inputs["signal_yellow_feedback"] = [feedback?.Signal == SignalState.Yellow];
        inputs["manual_red"] = [block.ManualRed];
        inputs["has_crossing"] = [block.Definition.HasCrossing];
        inputs["crossing_closed"] = [feedback?.Crossing == CrossingState.Closed];
        inputs["has_switch"] = [block.Definition.NormalNextBlockId is not null];
        inputs["switch_known"] = [feedback?.Switch is SwitchPosition.Normal or SwitchPosition.Reverse];
        inputs["switch_confirmed"] = [feedback?.Switch == block.CommandedSwitch];
        inputs["reported_normal"] = [feedback?.Switch == SwitchPosition.Normal];
        inputs["reported_reverse"] = [feedback?.Switch == SwitchPosition.Reverse];
        inputs["reserved_known"] = [feedback?.IsReservedByCtc is not null];
        inputs["reserved"] = [feedback?.IsReservedByCtc == true];
    }

    private IReadOnlyDictionary<string, bool[]> SwitchPlc(Block block, SwitchPosition desired)
    {
        var inputs = PlcInputs();
        inputs["zone_ok_in"] = [true];
        inputs["requested_normal"] = [desired == SwitchPosition.Normal];
        inputs["switch_request_known"] = [desired is SwitchPosition.Normal or SwitchPosition.Reverse];
        inputs["movement_locked"] = [_snapshot?.Authorizations.Any(authorization => authorization.AuthorizedAuthorityMeters > 0 && new[] { authorization.TargetBlockId }.Concat(authorization.PathBlockIds).Intersect(block.Definition.SwitchProtectionBlockIds).Any()) == true];
        IReadOnlyDictionary<string, bool[]>? outputs = null;
        foreach (string id in block.Definition.SwitchProtectionBlockIds)
        {
            SetBlockInputs(inputs, GetBlock(id));
            outputs = _plc!.Execute(inputs);
            inputs["zone_ok_in"] = outputs["zone_ok"];
        }
        return outputs ?? _plc!.Execute(inputs);
    }

    private IReadOnlyDictionary<string, bool[]> CrossingPlc(Block block)
    {
        var inputs = PlcInputs();
        inputs["active_route"] = [_requests.Values.Any(request => IsActive(request) && GetRequestPath(request).Contains(block.Definition.BlockId))];
        inputs["manual_crossing_close"] = [block.ManualCrossingClosed];
        IReadOnlyDictionary<string, bool[]>? outputs = null;
        foreach (string id in block.Definition.CrossingProtectionBlockIds)
        {
            SetBlockInputs(inputs, GetBlock(id));
            outputs = _plc!.Execute(inputs);
            inputs["crossing_needed_in"] = outputs["crossing_needed"];
        }
        return outputs ?? _plc!.Execute(inputs);
    }

    private bool ClosurePlc(Block block)
    {
        if (!_hardwareMode)
        {
            return IsFresh(block) && block.Feedback?.Occupancy == OccupancyState.Clear;
        }
        if (_plc is null)
        {
            return false;
        }
        var inputs = PlcInputs();
        SetBlockInputs(inputs, block);
        bool requested = _plc.Execute(inputs)["closure_closed"][0];
        // Independent veto: an occupied or unavailable block cannot be reported closed.
        return requested && IsFresh(block) && block.Feedback?.Occupancy == OccupancyState.Clear;
    }

    private WaysideAuthorization EvaluateVital(Request receivedRequest)
    {
        var request = receivedRequest.Value;
        var output = new WaysideAuthorization(request.RequestId, request.TrainId, request.TargetBlockId, request.EntryBlockId, request.PathBlockIds.AsReadOnly(), request.SuggestedSpeedMetersPerSecond, request.SuggestedAuthorityMeters, 0, 0, "Withheld", "");
        if (_plc is null)
        {
            return Stop(output, "No valid PLC program.");
        }
        try
        {
            var path = GetRequestPath(receivedRequest).ToArray();
            var inputs = PlcInputs();
            inputs["request_fresh"] = [IsActive(receivedRequest)];
            inputs["request_retired"] = [_retiredRequests.Contains(request.RequestId)];
            inputs["origin_occupied"] = [GetBlock(request.TargetBlockId).Feedback?.Occupancy == OccupancyState.Occupied];
            inputs["route_conflict"] = [_requests.Values.Any(other => other != receivedRequest && IsActive(other) && GetRequestPath(other).Intersect(path).Any())];
            inputs["suggested_speed"] = PlcBits.Encode(request.SuggestedSpeedMetersPerSecond);
            inputs["suggested_authority"] = PlcBits.Encode(request.SuggestedAuthorityMeters);
            inputs["speed_in"] = inputs["suggested_speed"];
            inputs["remaining_in"] = inputs["suggested_authority"];
            inputs["scan_live"] = [true];
            string reason = "PLC protected the requested route.";
            for (int i = 0; i < path.Length; i++)
            {
                var block = GetBlock(path[i]);
                SetBlockInputs(inputs, block);
                inputs["is_origin"] = [i == 0];
                inputs["block_length"] = PlcBits.Encode(block.Definition.LengthMeters);
                inputs["track_limit"] = PlcBits.Encode(block.Definition.SpeedLimitMetersPerSecond);
                inputs["has_next"] = [i + 1 < path.Length];
                inputs["has_stem"] = [block.Definition.StemBlockId is not null];
                string? entry = i == 0 ? request.EntryBlockId : path[i - 1];
                string? exit = i + 1 < path.Length ? path[i + 1] : null;
                inputs["entry_stem"] = [entry is not null && entry == block.Definition.StemBlockId];
                inputs["exit_stem"] = [exit is not null && exit == block.Definition.StemBlockId];
                inputs["entry_normal"] = [entry is not null && entry == block.Definition.NormalNextBlockId];
                inputs["entry_reverse"] = [entry is not null && entry == block.Definition.ReverseNextBlockId];
                inputs["exit_normal"] = [exit is not null && exit == block.Definition.NormalNextBlockId];
                inputs["exit_reverse"] = [exit is not null && exit == block.Definition.ReverseNextBlockId];
                var step = _plc.Execute(inputs);
                inputs["distance_in"] = step["route_distance"];
                inputs["remaining_in"] = step["route_remaining"];
                inputs["speed_in"] = step["route_speed"];
                inputs["fault_in"] = step["route_fault"];
                inputs["yellow_in"] = step["route_yellow"];
                inputs["signal_in"] = step["route_signal"];
                inputs["scan_live"] = step["route_continue"];
                if (!step["route_continue"][0])
                {
                    reason = PlcRestrictionText(inputs, block, step, exit);
                    break;
                }
            }
            var final = _plc.Execute(inputs);
            double speed = PlcBits.Decode(final["authorized_speed"]);
            double authority = PlcBits.Decode(final["authorized_authority"]);
            output = output with
            {
                AuthorizedSpeedMetersPerSecond = speed,
                AuthorizedAuthorityMeters = authority,
                Status = authority <= 0 || speed <= 0 ? "Withheld" : authority < request.SuggestedAuthorityMeters || speed < request.SuggestedSpeedMetersPerSecond ? "Restricted" : "Approved",
                Reason = reason
            };
            if (!final["allow_route"][0] && (speed > 0 || authority > 0))
            {
                return Stop(output, "Independent safety check rejected contradictory PLC movement outputs.");
            }
            // Only PLC bits choose the normal aspect. Manual red remains an independent veto.
            foreach (string id in GetCoveredBlocks(output))
            {
                var block = GetBlock(id);
                if (block.Definition.HasSignal && !block.ManualRed)
                {
                    block.CommandedSignal = !final["signal_clear"][0] ? SignalState.Red : final["signal_yellow"][0] ? SignalState.Yellow : SignalState.Green;
                }
            }
            // Session/lease checks are independent vetoes, never a source of positive commands.
            if (_paused || _maintenanceMode || !IsActive(receivedRequest) || _retiredRequests.Contains(request.RequestId) || inputs["route_conflict"][0])
            {
                return Stop(output, reason);
            }
            return output;
        }
        catch (ArgumentException ex)
        {
            return Stop(output, $"PLC input/output rejected: {ex.Message}");
        }
    }

    private static string PlcRestrictionText(IReadOnlyDictionary<string, bool[]> inputs, Block block, IReadOnlyDictionary<string, bool[]> outputs, string? nextBlock)
    {
        // Explanations mirror input facts; they do not change the PLC's output values.
        if (!inputs["running"][0])
        {
            return "Simulation paused.";
        }
        if (inputs["maintenance"][0])
        {
            return "Maintenance mode is active.";
        }
        if (!inputs["request_fresh"][0])
        {
            return "CTC request expired; refresh it or submit a new target block and route.";
        }
        if (inputs["request_retired"][0])
        {
            return "Target block cleared; a new request and current target block are required.";
        }
        if (!inputs["origin_occupied"][0])
        {
            return "Target block is not occupied. Submit a new origin after movement.";
        }
        if (inputs["route_conflict"][0])
        {
            return "Conflicting active movement requests overlap this route.";
        }
        if (!inputs["feedback_fresh"][0] || inputs["block_unknown"][0])
        {
            return $"Block {block.Definition.BlockId} feedback is missing, stale, or unknown.";
        }
        if (inputs["block_faulted"][0])
        {
            return $"Block {block.Definition.BlockId} reports a fault.";
        }
        if (inputs["has_switch"][0] && outputs["route_fault"][0])
        {
            return $"Waiting for switch {block.Definition.BlockId} alignment feedback; this request needs {nextBlock ?? "the current position"} (verify entry and alignment).";
        }
        if (inputs["has_signal"][0] && (!inputs["signal_proceed"][0] || inputs["manual_red"][0]))
        {
            return $"Waiting for proceed signal feedback at block {block.Definition.BlockId}.";
        }
        if (inputs["has_crossing"][0] && !inputs["crossing_closed"][0])
        {
            return $"Waiting for crossing {block.Definition.BlockId} to report closed.";
        }
        if (inputs["block_closed"][0] || (!inputs["is_origin"][0] && (!inputs["block_clear"][0] || inputs["block_pending"][0])))
        {
            return $"Authority ends before block {block.Definition.BlockId} ({(block.Maintenance != "Open" ? block.Maintenance : "occupied")}).";
        }
        return "PLC evaluated the requested route.";
    }
}
