using CTC.Core.Models;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace CTC.Core.Dispatching;

/// <summary>
/// The complete route context of one movement request, exactly as the Track Controller requires it:
/// the train's current block as the target, the block it entered from, the blocks ahead that the
/// authority covers, and a fixed authority measured from the target block's entry
/// (<see cref="MovementSuggestionMessage.BlockEntryReference"/>).
/// </summary>
public sealed record MovementRequestPlan(
    string TrainId,
    string TargetBlockId,
    string? EntryBlockId,
    IReadOnlyList<string> PathBlockIds,
    double SpeedMetersPerSecond,
    double AuthorityMeters)
{
    private const double Tolerance = 1e-9;

    /// <summary>Same route context and limits: a renewal of it keeps the request ID.</summary>
    public bool Matches(MovementRequestPlan other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return TrainId == other.TrainId
            && TargetBlockId == other.TargetBlockId
            && EntryBlockId == other.EntryBlockId
            && PathBlockIds.SequenceEqual(other.PathBlockIds)
            && Math.Abs(SpeedMetersPerSecond - other.SpeedMetersPerSecond) < Tolerance
            && Math.Abs(AuthorityMeters - other.AuthorityMeters) < Tolerance;
    }

    /// <param name="predecessorRequestId">For a successor: the active request it is staged behind; null for an ordinary request.</param>
    public MovementSuggestionMessage ToMessage(string requestId, string? predecessorRequestId = null) => new MovementSuggestionMessage
    {
        PredecessorRequestId = predecessorRequestId,
        RequestId = requestId,
        TrainId = TrainId,
        TargetBlockId = TargetBlockId,
        EntryBlockId = EntryBlockId,
        AuthorityReference = MovementSuggestionMessage.BlockEntryReference,
        PathBlockIds = PathBlockIds.ToList(),
        SuggestedSpeedMetersPerSecond = SpeedMetersPerSecond,
        SuggestedAuthorityMeters = AuthorityMeters,
        IsActive = true,
    };

    /// <summary>Cancels this request at its target block. The Track Controller matches the ID.</summary>
    public MovementSuggestionMessage ToCancellation(string requestId) => new MovementSuggestionMessage
    {
        RequestId = requestId,
        TrainId = TrainId,
        TargetBlockId = TargetBlockId,
        EntryBlockId = EntryBlockId,
        AuthorityReference = MovementSuggestionMessage.BlockEntryReference,
        PathBlockIds = PathBlockIds.ToList(),
        SuggestedSpeedMetersPerSecond = 0,
        SuggestedAuthorityMeters = 0,
        IsActive = false,
    };
}

/// <summary>
/// Decides which movement request a dispatched train should hold right now. Pure: it reads CTC
/// state and never sends anything (see <see cref="MovementRequestCoordinator"/>).
/// </summary>
/// <remarks>
/// <para>
/// The request targets the train's current tracked block, enters from the block before it, and
/// covers the route blocks ahead up to the movement's fixed authority end
/// (<see cref="DispatchedTrainState.AuthorityLimitRouteIndex"/>), stopping before the first block
/// that is occupied, of unknown or stale occupancy, or closed (requested or confirmed). The
/// authority is the length of the covered blocks, from the target block's entry, so as the train
/// advances the same fixed end point is expressed from its new block: the original allowance is
/// never restarted and never extended.
/// </para>
/// <para>
/// A train holds no request while it is awaiting placement (CTC has not seen it on the track),
/// once its plan is completed, or when even its own block is closed. The Track Controller remains
/// the final authority and re-checks all of this.
/// </para>
/// </remarks>
public static class MovementRequestPlanner
{
    /// <summary>Whether the train should hold an active movement request at all.</summary>
    public static bool IsEligible(DispatchedTrainState train, TrainMovementPhase phase) =>
        train.CurrentBlockOccupancyObserved && phase != TrainMovementPhase.Completed;

    /// <summary>The request <paramref name="train"/> should hold now, or null.</summary>
    public static MovementRequestPlan? Plan(CtcLineState line, DispatchedTrainState train)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(train);

        return IsEligible(train, train.Phase)
            ? Plan(line, train.TrainId, train.ActiveRouteBlockIds.ToList(), train.ActiveRouteIndex, train.AuthorityLimitRouteIndex, train.PreviousBlockId, train.SuggestedSpeedMetersPerSecond)
            : null;
    }

    /// <summary>
    /// The successor <paramref name="train"/> should have prepared behind <paramref name="current"/>:
    /// exactly the request it will need once it enters its next route block, re-expressing the same
    /// fixed end point from there. Null unless the Track Controller actually grants
    /// <paramref name="current"/> with authority reaching into that next block (a prepared successor
    /// must never be a way around a withheld or shortened grant).
    /// </summary>
    public static MovementRequestPlan? PlanSuccessor(CtcLineState line, DispatchedTrainState train, MovementRequestState current)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(train);
        ArgumentNullException.ThrowIfNull(current);

        const double tolerance = 1e-6;
        if (!IsEligible(train, train.Phase) || current.Plan.PathBlockIds.Count == 0
            || current.Acknowledgement is null or RequestAcknowledgementOutcome.Rejected || !current.IsMovementAuthorized)
        {
            return null;
        }

        var block = line.Blocks.FirstOrDefault(candidate => candidate.BlockId == current.Plan.TargetBlockId);
        var route = train.ActiveRouteBlockIds.ToList();
        int index = train.ActiveRouteIndex;
        if (block is null || current.AuthorizedAuthorityMeters <= block.LengthMeters + tolerance
            || index < 0 || index + 1 >= route.Count || route[index] != current.Plan.TargetBlockId || route[index + 1] != current.Plan.PathBlockIds[0])
        {
            return null;
        }

        var successor = Plan(line, train.TrainId, route, index + 1, train.AuthorityLimitRouteIndex, current.Plan.TargetBlockId, train.SuggestedSpeedMetersPerSecond);

        // What the Track Controller accepts as a successor: the next block, entered from this one,
        // along (never beyond) this route and end point, at no higher speed.
        var remainder = current.Plan.PathBlockIds.Skip(1).ToList();
        return successor is not null
            && successor.TargetBlockId == current.Plan.PathBlockIds[0]
            && successor.EntryBlockId == current.Plan.TargetBlockId
            && successor.PathBlockIds.Count <= remainder.Count
            && successor.PathBlockIds.SequenceEqual(remainder.Take(successor.PathBlockIds.Count))
            && successor.AuthorityMeters <= current.Plan.AuthorityMeters - block.LengthMeters + tolerance
            && successor.SpeedMetersPerSecond <= current.Plan.SpeedMetersPerSecond + tolerance
            ? successor
            : null;
    }

    /// <summary>
    /// The request for a train at <paramref name="currentIndex"/> of <paramref name="routeBlockIds"/>
    /// whose movement's authority ends at <paramref name="limitIndex"/>; null when no block can be
    /// covered or a route block is no longer in the layout.
    /// </summary>
    public static MovementRequestPlan? Plan(
        CtcLineState line,
        string trainId,
        IReadOnlyList<string> routeBlockIds,
        int currentIndex,
        int limitIndex,
        string? previousBlockId,
        double speedMetersPerSecond)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(routeBlockIds);

        if (currentIndex < 0 || currentIndex >= routeBlockIds.Count || limitIndex < currentIndex)
        {
            return null;
        }

        int last = Math.Min(limitIndex, routeBlockIds.Count - 1);
        var blocks = new List<CtcBlockState>();
        var included = new HashSet<string>(StringComparer.Ordinal);
        for (int i = currentIndex; i <= last; i++)
        {
            // One request never revisits a block (the Track Controller refuses it); a route that does is
            // covered segment by segment (MovementSegmentation), so this only guards that.
            if (!included.Add(routeBlockIds[i]))
            {
                break;
            }

            var block = line.Blocks.FirstOrDefault(candidate => candidate.BlockId == routeBlockIds[i]);
            if (block is null)
            {
                return null;
            }

            blocks.Add(block);
        }

        int covered = AuthorityManager.CountAuthorizedBlocks(blocks, trainOccupiesStartBlock: true);
        if (covered == 0)
        {
            return null;
        }

        string? entry = currentIndex > 0 ? routeBlockIds[currentIndex - 1] : previousBlockId;
        return new MovementRequestPlan(
            trainId,
            blocks[0].BlockId,
            string.IsNullOrEmpty(entry) ? null : entry,
            blocks.Skip(1).Take(covered - 1).Select(block => block.BlockId).ToList(),
            double.IsFinite(speedMetersPerSecond) && speedMetersPerSecond > 0 ? speedMetersPerSecond : 0,
            blocks.Take(covered).Sum(block => block.LengthMeters));
    }
}
