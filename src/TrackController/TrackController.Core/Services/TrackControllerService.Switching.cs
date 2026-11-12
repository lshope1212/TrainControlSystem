using TrackController.Contracts.Messages;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;
using MovementResult = (TrainControl.Contracts.Messages.MovementSuggestionMessage Request, double AuthorizedSpeedMetersPerSecond, double AuthorizedAuthorityMeters, string Status, string Reason);

namespace TrackController.Core.Services;

/// <summary>
/// Automatic switch alignment for accepted CTC routes, and the CTC reservations that protect it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Route demand.</b> An accepted, live CTC movement request names the train's block (its target),
/// the block it entered from and every block of its route ahead. For each owned switch on that route
/// the position the route needs follows from the topology alone: crossing between a switch block and
/// one of its legs (<see cref="WaysideBlockDefinition.NormalNextBlockId"/> /
/// <see cref="WaysideBlockDefinition.ReverseNextBlockId"/>) needs the switch set to that leg, in either
/// direction of travel; crossing to or from the stem needs nothing; leg-to-leg is not a route and
/// demands nothing (the route evaluation withholds it). The controller then commands that position
/// itself, through its normal switch decision: the PLC's <c>switch_move</c> (hardware mode) and the
/// independent <see cref="GetSwitchProtectionReason"/> veto (both modes) must permit it, exactly as for
/// a dispatcher's switch request. CTC never commands a switch to make a route work.
/// </para>
/// <para>
/// <b>Reservations.</b> A CTC reservation is an accepted movement request: CTC reserves the request's
/// target and route blocks by sending it, keeps the reservation by renewing it, and releases it by
/// cancelling it. The controller retires it when its target block reports clear (the train moved on;
/// its successor holds the route ahead). A request that expired without being cancelled (CTC stopped
/// renewing it) is stale and keeps reserving: it no longer authorizes anything, but nothing may be
/// assumed about the route its train was given. Every movement request for a block is routed to the
/// block's owning controller and a switch's protection zone must lie in that same territory, so this
/// controller's request table is the complete reservation record for every zone it protects. The
/// Track Model has no knowledge of CTC routes; a reservation it does report
/// (<see cref="TrackModelBlockStateMessage.IsReservedByCtc"/> = true) is honoured as an additional one.
/// </para>
/// <para>
/// A switch may move only when no reservation other than the route asking for that position holds
/// any block of its zone: the requesting route's own pending reservation never locks the switch it
/// needs, so it cannot deadlock itself. Once that route is granted, the grant itself locks the zone
/// (<c>movement_locked</c>) until the train has passed.
/// </para>
/// <para>
/// <b>Throw, confirmation and holding.</b> The commanded position is recomputed on every evaluation. A
/// throw that has been commanded but not yet reported is completed (never reversed) when conditions
/// change mid-stroke; one the Track Model has not confirmed within
/// <see cref="SwitchConfirmationSeconds"/> is reported as unconfirmed and, if moving is no longer
/// permitted, the command falls back to the reported position. With no route demand the switch holds
/// its position, so a finished route never "resets" a switch. Movement through a switch is authorized
/// only on the Track Model's reported position equal to the commanded one.
/// </para>
/// <para>
/// <b>Approach locking.</b> When an authority over a switch's protection zone is withdrawn without its
/// train having moved on (cancelled, re-routed, revoked or expired), that train may still be braking
/// toward the switch. The switch then stays locked for <see cref="SwitchApproachLockSeconds"/>.
/// </para>
/// </remarks>
public sealed partial class TrackControllerService
{
    /// <summary>Wall-clock seconds a switch stays locked after an authority over its protection zone is withdrawn short of the train passing.</summary>
    public const double SwitchApproachLockSeconds = 10;

    /// <summary>Wall-clock seconds the Track Model has to report a commanded switch position before the throw is reported unconfirmed.</summary>
    public const double SwitchConfirmationSeconds = 5;

    /// <summary>What the live routes need of one switch: one request's position, or <see cref="SwitchPosition.Unknown"/> when several routes claim it.</summary>
    private sealed record SwitchDemand(SwitchPosition Position, IReadOnlyList<MovementSuggestionMessage> Requests);

    /// <summary>
    /// The position a route needs a switch in, from the blocks it enters the switch block from and leaves it to.
    /// Null when the route needs no particular position (it only touches the stem) or is not a path (leg to leg).
    /// </summary>
    internal static SwitchPosition? RequiredSwitchPosition(WaysideBlockDefinition definition, string? entryBlockId, string? exitBlockId)
    {
        if (definition.NormalNextBlockId is null)
        {
            return null;
        }

        SwitchPosition? Leg(string? id) => id is null ? null
            : id == definition.NormalNextBlockId ? SwitchPosition.Normal
            : id == definition.ReverseNextBlockId ? SwitchPosition.Reverse
            : null;

        SwitchPosition? entry = Leg(entryBlockId), exit = Leg(exitBlockId);
        return entry is not null && exit is not null ? null : entry ?? exit;
    }

    /// <summary>A request may set its route only while it is live, unretired, and its train is observed in its target block.</summary>
    private bool CanSetRoute((MovementSuggestionMessage Value, DateTimeOffset Received) request)
    {
        var origin = GetBlock(request.Value.TargetBlockId);
        return IsActive(request) && !_retiredRequests.Contains(request.Value.RequestId)
            && IsFresh(origin) && origin.Feedback!.Occupancy == OccupancyState.Occupied && !origin.Feedback.IsFaulted;
    }

    /// <summary>Every owned switch some live route needs, keyed by switch block.</summary>
    private Dictionary<string, SwitchDemand> ComputeSwitchDemands()
    {
        var claims = new Dictionary<string, List<(MovementSuggestionMessage Request, SwitchPosition Position)>>(StringComparer.Ordinal);
        foreach (var request in _requests.Values.Where(CanSetRoute))
        {
            var path = GetRequestPath(request).ToArray();
            for (int i = 0; i < path.Length; i++)
            {
                var block = GetBlock(path[i]);
                string? entry = i > 0 ? path[i - 1] : request.Value.EntryBlockId;
                string? exit = i + 1 < path.Length ? path[i + 1] : null;
                if (_owned.Contains(path[i]) && RequiredSwitchPosition(block.Definition, entry, exit) is { } position)
                {
                    if (!claims.TryGetValue(path[i], out var list))
                    {
                        claims[path[i]] = list = [];
                    }

                    list.Add((request.Value, position));
                }
            }
        }

        return claims.ToDictionary(claim => claim.Key, claim => claim.Value.Count == 1
            ? new SwitchDemand(claim.Value[0].Position, [claim.Value[0].Request])
            : new SwitchDemand(SwitchPosition.Unknown, claim.Value.Select(item => item.Request).ToList()), StringComparer.Ordinal);
    }

    /// <summary>
    /// Decides this evaluation's switch command: the route's position when moving is permitted, the
    /// stroke in progress when one is, otherwise the reported position.
    /// </summary>
    private void CommandSwitch(Block block, SwitchDemand? demand, DateTimeOffset now)
    {
        var reported = IsFresh(block) ? block.Feedback!.Switch : SwitchPosition.Unknown;
        var previous = block.CommandedSwitch;
        bool inTransit = previous is SwitchPosition.Normal or SwitchPosition.Reverse && previous != reported
            && block.SwitchThrowStartedAt is { } started && (now - started).TotalSeconds <= SwitchConfirmationSeconds;
        var hold = inTransit ? previous : reported;

        // Maintenance mode hands the switches to the maintainer; routes are not set automatically then.
        var routeDemand = _maintenanceMode ? null : demand;
        string? requester = null;
        SwitchPosition desired;
        if (routeDemand is { Position: SwitchPosition.Normal or SwitchPosition.Reverse })
        {
            desired = routeDemand.Position;
            requester = routeDemand.Requests[0].RequestId;
            // The route supersedes an older standing request for the other position, which must not
            // pull the switch back once this route is finished.
            if (block.DesiredSwitch is SwitchPosition.Normal or SwitchPosition.Reverse && block.DesiredSwitch != desired)
            {
                block.DesiredSwitch = SwitchPosition.Unknown;
            }
        }
        else
        {
            // Conflicting routes hold the switch; otherwise a dispatcher's or maintainer's request applies.
            desired = routeDemand is null ? block.DesiredSwitch : SwitchPosition.Unknown;
        }

        if (desired is not (SwitchPosition.Normal or SwitchPosition.Reverse))
        {
            desired = hold;
        }

        string? blocked = null;
        bool permitted = false;
        if (desired is SwitchPosition.Normal or SwitchPosition.Reverse)
        {
            blocked = GetSwitchProtectionReason(block, requester, automatic: routeDemand is not null);
            bool plcMove = !_hardwareMode;
            if (_hardwareMode && _plc is not null)
            {
                var outputs = SwitchPlc(block, desired, requester);
                desired = outputs["switch_normal"][0] ? SwitchPosition.Normal : SwitchPosition.Reverse;
                plcMove = outputs["switch_move"][0];
            }

            permitted = plcMove && blocked is null && (!_paused || _maintenanceMode);
            if (!permitted && blocked is null)
            {
                blocked = _paused && !_maintenanceMode ? "the simulation is paused"
                    : _hardwareMode && _plc is null ? "no valid PLC program is loaded"
                    : "the PLC withholds switch movement";
            }
        }

        var commanded = permitted ? desired : hold;
        if (commanded is SwitchPosition.Normal or SwitchPosition.Reverse && commanded != reported)
        {
            if (commanded != previous || block.SwitchThrowStartedAt is null)
            {
                block.SwitchThrowStartedAt = now;
            }
        }
        else
        {
            block.SwitchThrowStartedAt = null;
        }

        block.CommandedSwitch = commanded;
        block.SwitchStatus = DescribeSwitch(block, routeDemand, reported, blocked, now);
    }

    /// <summary>Operator-readable switch alignment state for movement reasons; explanation only.</summary>
    private string DescribeSwitch(Block block, SwitchDemand? demand, SwitchPosition reported, string? blocked, DateTimeOffset now)
    {
        if (demand is null)
        {
            return string.Empty;
        }

        string id = block.Definition.BlockId;
        if (demand.Position is not (SwitchPosition.Normal or SwitchPosition.Reverse))
        {
            return $"switch {id} is claimed by {demand.Requests.Count} routes ({string.Join(", ", demand.Requests.Select(Describe))}); it is held {block.CommandedSwitch} until only one route needs it";
        }

        var needed = demand.Position;
        if (block.CommandedSwitch == needed && reported == needed)
        {
            return $"switch {id} is set {needed} for this route";
        }

        if (block.CommandedSwitch == needed)
        {
            double elapsed = block.SwitchThrowStartedAt is { } started ? (now - started).TotalSeconds : 0;
            return elapsed > SwitchConfirmationSeconds
                ? $"switch {id} was commanded {needed} more than {SwitchConfirmationSeconds:0} s ago, but the Track Model still reports {reported}: the throw is not confirmed"
                : $"switch {id} commanded {needed} for this route; waiting for the Track Model to report {needed}, it reports {reported}";
        }

        string prefix = $"Switch {id} locked: ";
        string why = blocked is null ? "the PLC withholds switch movement"
            : (blocked.StartsWith(prefix, StringComparison.Ordinal) ? blocked[prefix.Length..] : blocked).TrimEnd('.');
        return $"switch {id} must be {needed} for this route, but it cannot move now: {why}";
    }

    /// <summary>Dispatcher-readable name of a request in reasons.</summary>
    private static string Describe(MovementSuggestionMessage request) =>
        $"{(string.IsNullOrEmpty(request.TrainId) ? "a CTC route" : $"train {request.TrainId}")} (request {(request.RequestId.Length > 8 ? request.RequestId[..8] : request.RequestId)})";

    /// <summary>
    /// The CTC reservation, other than <paramref name="requesterId"/>'s own, that holds <paramref name="zone"/>;
    /// null when there is none.
    /// </summary>
    private string? ReservationHolder(Block zone, string? requesterId)
    {
        if (zone.Feedback?.IsReservedByCtc == true)
        {
            return "a reservation reported in its track feedback";
        }

        string id = zone.Definition.BlockId;
        foreach (var request in _requests.Values)
        {
            if (request.Value.RequestId != requesterId && !_retiredRequests.Contains(request.Value.RequestId) && GetRequestPath(request).Contains(id))
            {
                return Describe(request.Value) + (IsActive(request) ? string.Empty : ", whose request expired without being cancelled");
            }
        }

        return null;
    }

    /// <summary>Whether any CTC reservation holds the block (operator display).</summary>
    private bool IsReserved(Block block) => ReservationHolder(block, requesterId: null) is not null;

    /// <summary>A positive authority from the previous evaluation runs through the switch's protection zone.</summary>
    private bool IsMovementLocked(Block block) =>
        _snapshot?.Authorizations.Any(authorization => authorization.AuthorizedAuthorityMeters > 0
            && new[] { authorization.Request!.TargetBlockId }.Concat(authorization.Request!.PathBlockIds).Intersect(block.Definition.SwitchProtectionBlockIds).Any()) == true;

    private bool IsApproachLocked(Block block) => block.SwitchApproachLockedUntil is { } until && _clock.GetUtcNow() < until;

    /// <summary>
    /// Arms approach locking for every switch whose protection zone a grant of the previous evaluation
    /// covered, when that grant is no longer in force and its request was not retired by the train moving on.
    /// </summary>
    private void ArmApproachLocks(IReadOnlyList<MovementResult> previous, IReadOnlyList<MovementResult> current, DateTimeOffset now)
    {
        var granted = current.Where(result => result.AuthorizedAuthorityMeters > 0).Select(result => result.Request.RequestId).ToHashSet(StringComparer.Ordinal);
        foreach (var withdrawn in previous.Where(result => result.AuthorizedAuthorityMeters > 0 && !granted.Contains(result.Request.RequestId) && !_retiredRequests.Contains(result.Request.RequestId)))
        {
            var path = new[] { withdrawn.Request.TargetBlockId }.Concat(withdrawn.Request.PathBlockIds).ToHashSet(StringComparer.Ordinal);
            foreach (var block in _blocks.Values.Where(block => block.Definition.NormalNextBlockId is not null && _owned.Contains(block.Definition.BlockId) && block.Definition.SwitchProtectionBlockIds.Any(path.Contains)))
            {
                block.SwitchApproachLockedUntil = now.AddSeconds(SwitchApproachLockSeconds);
            }
        }
    }

    /// <summary>The route-alignment detail for a movement reason that waits on <paramref name="block"/>'s switch.</summary>
    private static string SwitchWaitDetail(Block block) =>
        block.SwitchStatus.Length > 0 ? block.SwitchStatus : "verify entry and alignment";
}
