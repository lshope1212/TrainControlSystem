using TrackController.Contracts.Messages;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;
using MovementResult = (TrainControl.Contracts.Messages.MovementSuggestionMessage Request, double AuthorizedSpeedMetersPerSecond, double AuthorizedAuthorityMeters, string Status, string Reason);

namespace TrackController.Core.Services;

/// <summary>What <see cref="TrackControllerService.SubmitMovement"/> did with a movement message.</summary>
public enum MovementSubmission
{
    /// <summary>An ordinary request (or a renewal of an active one) is held and evaluated.</summary>
    Accepted = 0,

    /// <summary>A successor was validated and staged behind its predecessor. It grants nothing.</summary>
    Staged,

    /// <summary>A cancellation was applied (or was a harmless repeat).</summary>
    Cancelled,
}

/// <summary>
/// Movement authority handoff at block boundaries: prepare (stage) a successor, transfer authority to
/// it atomically when the train is observed crossing into its target, retire the predecessor.
/// </summary>
/// <remarks>
/// <para>
/// Why: requests are evaluated from the train's occupied block (the target must be occupied) and two
/// overlapping requests withhold each other. Replacing a request only after the train moved therefore
/// always left a gap with no authority. Staging keeps the successor out of evaluation and conflict
/// checks until the transfer; the transfer swaps predecessor and successor inside one lock, so no
/// evaluation ever sees both (a conflict) or neither (a gap).
/// </para>
/// <para>
/// Safety: the transfer adds no authority. A successor may only re-express the predecessor's own end
/// point from the next block (checked when it is staged), it is promoted only from a predecessor whose
/// grant covered the successor's target at the moment of the transition, and once promoted it is
/// evaluated exactly like any other request (PLC and independent checks). Any failed condition leaves
/// the train without a transfer, so its authority is withheld, never forced.
/// </para>
/// </remarks>
public sealed partial class TrackControllerService
{
    private const double HandoffTolerance = 1e-6;

    /// <summary>Validates <paramref name="message"/> as the successor of the active request <paramref name="predecessorId"/> and stages it.</summary>
    private void StageSuccessor(MovementSuggestionMessage message, string predecessorId)
    {
        if (predecessorId == message.RequestId)
        {
            throw new ArgumentException("A successor needs its own request ID.");
        }

        var predecessor = _requests.Values.FirstOrDefault(request => request.Value.RequestId == predecessorId);
        if (predecessor.Value is null || !IsActive(predecessor) || _retiredRequests.Contains(predecessorId))
        {
            throw new ArgumentException($"Predecessor request {predecessorId} is not active here, so no successor can be prepared. Submit an ordinary request from the train's current block.");
        }

        var previous = predecessor.Value;
        if (previous.PathBlockIds.Count == 0 || message.TargetBlockId != previous.PathBlockIds[0])
        {
            throw new ArgumentException(previous.PathBlockIds.Count == 0
                ? "The predecessor's route ends in its own block; there is no next block to hand off to."
                : $"A successor must target the predecessor's next block {previous.PathBlockIds[0]}.");
        }

        if (message.EntryBlockId != previous.TargetBlockId)
        {
            throw new ArgumentException($"A successor must enter from the predecessor's block {previous.TargetBlockId}.");
        }

        var remainder = previous.PathBlockIds.Skip(1).ToList();
        if (message.PathBlockIds.Count > remainder.Count || !message.PathBlockIds.SequenceEqual(remainder.Take(message.PathBlockIds.Count)))
        {
            throw new ArgumentException("A successor's route must continue the predecessor's route and never go beyond it.");
        }

        // The same fixed end point expressed from the next block: never further.
        double endPoint = previous.SuggestedAuthorityMeters - GetBlock(previous.TargetBlockId).Definition.LengthMeters;
        if (message.SuggestedAuthorityMeters > endPoint + HandoffTolerance)
        {
            throw new ArgumentException($"A successor cannot move the predecessor's end point: at most {Math.Max(0, endPoint):0.###} m from block {message.TargetBlockId}'s entry.");
        }

        if (message.SuggestedSpeedMetersPerSecond > previous.SuggestedSpeedMetersPerSecond + HandoffTolerance)
        {
            throw new ArgumentException("A successor cannot raise the predecessor's speed.");
        }

        if (!string.Equals(message.TrainId, previous.TrainId, StringComparison.Ordinal))
        {
            throw new ArgumentException("A successor must carry its predecessor's train label.");
        }

        // One successor per predecessor. A different one (e.g. after a re-route) replaces it, and the
        // replaced ID is retired so a delayed copy cannot come back.
        foreach (string replaced in _staged.Values.Where(staged => staged.Value.PredecessorRequestId == predecessorId && staged.Value.RequestId != message.RequestId)
                     .Select(staged => staged.Value.RequestId).ToList())
        {
            _staged.Remove(replaced);
            _retiredRequests.Add(replaced);
        }

        _staged[message.RequestId] = (new MovementSuggestionMessage
        {
            RequestId = message.RequestId,
            TrainId = message.TrainId,
            TargetBlockId = message.TargetBlockId,
            EntryBlockId = message.EntryBlockId,
            PathBlockIds = [.. message.PathBlockIds],
            SuggestedAuthorityMeters = message.SuggestedAuthorityMeters,
            SuggestedSpeedMetersPerSecond = message.SuggestedSpeedMetersPerSecond,
            PredecessorRequestId = predecessorId,
        }, _clock.GetUtcNow());
    }

    /// <summary>
    /// Promotes each staged successor whose transition the just-applied feedback shows, retiring its
    /// predecessor in the same step. Called inside the lock between applying a feedback batch and
    /// anything else that reads requests.
    /// </summary>
    /// <param name="applied">The blocks this batch updated.</param>
    /// <param name="clearBefore">Blocks whose feedback was fresh, Clear and unfaulted before this batch.</param>
    /// <param name="grantsBefore">Positive grants in force before this batch, by request ID.</param>
    private void TransferStagedAuthority(IReadOnlyList<Block> applied, IReadOnlySet<string> clearBefore, IReadOnlyDictionary<string, MovementResult> grantsBefore)
    {
        if (_staged.Count == 0)
        {
            return;
        }

        // Only an OBSERVED Clear -> Occupied transition of the successor's target counts: a block that
        // was stale, unknown or already occupied cannot show which train entered it.
        var entered = applied.Where(block => clearBefore.Contains(block.Definition.BlockId) && IsFresh(block) && block.Feedback!.Occupancy == OccupancyState.Occupied)
            .Select(block => block.Definition.BlockId).ToHashSet(StringComparer.Ordinal);
        var candidates = _staged.Values.Where(staged => entered.Contains(staged.Value.TargetBlockId)).ToList();
        foreach (var group in candidates.GroupBy(staged => staged.Value.TargetBlockId))
        {
            if (group.Count() != 1)
            {
                // Two prepared successors for one entered block: the occupancy cannot say whose train it
                // is, so neither takes over.
                foreach (var staged in group)
                {
                    _staged.Remove(staged.Value.RequestId);
                }

                continue;
            }

            var successor = group.Single();
            if (CanTransfer(successor.Value, clearBefore, grantsBefore))
            {
                var predecessor = _requests[successor.Value.EntryBlockId!];
                _requests.Remove(successor.Value.EntryBlockId!);
                _retiredRequests.Add(predecessor.Value.RequestId);
                _requests[successor.Value.TargetBlockId] = successor;
                // Carry the actual grant, not the larger original suggestion. Clearing a restriction
                // during the crossing must not move the endpoint or change the pre-coded continuation.
                double remaining = grantsBefore[predecessor.Value.RequestId].AuthorizedAuthorityMeters
                    - GetBlock(predecessor.Value.TargetBlockId).Definition.LengthMeters;
                GetBlock(successor.Value.TargetBlockId).Authority = new BlockAuthority(successor.Value,
                    Math.Min(remaining, successor.Value.SuggestedAuthorityMeters));
                _staged.Remove(successor.Value.RequestId);
            }
            else
            {
                // Not retired: if CTC later submits it as an ordinary request it is evaluated from scratch.
                _staged.Remove(successor.Value.RequestId);
            }
        }
    }

    /// <summary>Every condition for handing <paramref name="successor"/>'s predecessor's authority over to it.</summary>
    private bool CanTransfer(MovementSuggestionMessage successor, IReadOnlySet<string> clearBefore, IReadOnlyDictionary<string, MovementResult> grantsBefore)
    {
        string entry = successor.EntryBlockId!, target = successor.TargetBlockId;

        // The predecessor is still the live request of the block the train is leaving.
        if (!_requests.TryGetValue(entry, out var predecessor) || predecessor.Value.RequestId != successor.PredecessorRequestId
            || !IsActive(predecessor) || _retiredRequests.Contains(predecessor.Value.RequestId))
        {
            return false;
        }

        // It actually authorized movement into the target: the target was clear and protected for it,
        // so no other movement could have had authority there. Without that the train entered without
        // authority (e.g. after a revocation) and nothing is transferred.
        if (!grantsBefore.TryGetValue(predecessor.Value.RequestId, out var grant) || !GetCoveredBlocks(grant).Skip(1).Contains(target))
        {
            return false;
        }

        // Fresh, definite feedback on both sides of the boundary.
        var leaving = GetBlock(entry);
        var entering = GetBlock(target);
        if (!IsFresh(leaving) || leaving.Feedback!.Occupancy == OccupancyState.Unknown || leaving.Feedback.IsFaulted
            || entering.Feedback!.IsFaulted || entering.Feedback.IsClosed || entering.Maintenance == "Closed")
        {
            return false;
        }

        // Occupancy is anonymous, and train labels are never trusted to say who entered. If any other
        // block a train may enter the target from was not definitely clear, the occupancy could be that
        // train; it is attributed to the predecessor's train only if that train demonstrably left its
        // block in the same observation (a train cannot vanish).
        bool otherEntrantPossible = _blocks.Values.Any(block => block.Definition.BlockId != entry && block.Definition.BlockId != target
            && block.Definition.NextBlockIds.Contains(target) && !clearBefore.Contains(block.Definition.BlockId));
        if (otherEntrantPossible && leaving.Feedback.Occupancy != OccupancyState.Clear)
        {
            return false;
        }

        // No other request holds the target or any block of the successor's route.
        var route = new[] { target }.Concat(successor.PathBlockIds).ToArray();
        return !_requests.ContainsKey(target)
            && !_requests.Values.Any(other => other.Value.RequestId != predecessor.Value.RequestId && IsActive(other) && !_retiredRequests.Contains(other.Value.RequestId) && GetRequestPath(other).Intersect(route).Any());
    }

    /// <summary>Drops the staged successors of <paramref name="predecessorId"/> (its request was cancelled), retiring their IDs.</summary>
    private void DiscardSuccessorsOf(string predecessorId)
    {
        foreach (string id in _staged.Values.Where(staged => staged.Value.PredecessorRequestId == predecessorId).Select(staged => staged.Value.RequestId).ToList())
        {
            _staged.Remove(id);
            _retiredRequests.Add(id);
        }
    }

    /// <summary>
    /// Drops staged successors that can no longer take over: not renewed within the request watchdog,
    /// or whose predecessor is gone, expired or retired.
    /// </summary>
    private void PruneStaged()
    {
        foreach (var staged in _staged.Values.ToList())
        {
            var predecessor = _requests.Values.FirstOrDefault(request => request.Value.RequestId == staged.Value.PredecessorRequestId);
            if (!IsActive(staged) || predecessor.Value is null || !IsActive(predecessor) || _retiredRequests.Contains(predecessor.Value.RequestId))
            {
                _staged.Remove(staged.Value.RequestId);
            }
        }
    }

    /// <summary>
    /// The continuation code for each staged handoff whose predecessor holds a positive grant covering
    /// the successor's target: the predecessor's authority (and speed) from that block's entry, capped
    /// by what the successor asks for. Keyed by the successor's target block.
    /// </summary>
    private Dictionary<string, WaysideContinuation> ComputeContinuations(IReadOnlyList<MovementResult> results)
    {
        var continuations = new Dictionary<string, WaysideContinuation>(StringComparer.Ordinal);
        foreach (var result in results.Where(result => result.AuthorizedAuthorityMeters > 0 && result.AuthorizedSpeedMetersPerSecond > 0))
        {
            var successor = _staged.Values.FirstOrDefault(staged => staged.Value.PredecessorRequestId == result.Request.RequestId);
            if (successor.Value is null || !IsActive(successor) || !GetCoveredBlocks(result).Skip(1).Contains(successor.Value.TargetBlockId))
            {
                continue;
            }

            var next = GetBlock(successor.Value.TargetBlockId).Definition;
            double authority = Math.Min(result.AuthorizedAuthorityMeters - GetBlock(result.Request.TargetBlockId).Definition.LengthMeters, successor.Value.SuggestedAuthorityMeters);
            double speed = Math.Min(Math.Min(result.AuthorizedSpeedMetersPerSecond, successor.Value.SuggestedSpeedMetersPerSecond), next.SpeedLimitMetersPerSecond);
            if (authority > HandoffTolerance && speed > 0 && !continuations.ContainsKey(next.BlockId))
            {
                continuations[next.BlockId] = new WaysideContinuation(result.Request.RequestId, successor.Value.RequestId, result.Request.TargetBlockId, speed, authority);
            }
        }

        return continuations;
    }
}
