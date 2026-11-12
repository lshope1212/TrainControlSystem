using TrackModel.Core.Models;
using TrainControl.Contracts.Enums;

namespace TrackModel.Core.Services;

/// <summary>
/// Physical track topology of the loaded layout: which block a train's front can physically reach next,
/// and whether a route uses real connections and real switch branches. Pure: reads blocks, never changes them.
/// </summary>
/// <remarks>
/// <para>
/// A switch block S has three connections: its stem and two legs (<see cref="TrackBlock.NormalNextBlockId"/>,
/// <see cref="TrackBlock.ReverseNextBlockId"/>). The points join S to its legs, so a train passes the points
/// whenever it crosses the boundary between S and a leg, in either direction: that crossing is possible only
/// with S set to that leg. Crossing between S and its stem involves no points. Going from one leg straight
/// to the other through S is not a path.
/// </para>
/// <para>
/// Direction of travel (one-way sections) is an operating rule the Track Controller enforces for movement
/// authority; it is not a physical connection rule and is not checked here.
/// </para>
/// </remarks>
public static class TrackTopology
{
    /// <summary>The stem of switch block <paramref name="block"/>: its connection that is not a leg; null if not a switch.</summary>
    public static string? Stem(TrackBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (!block.HasSwitch) return null;
        var others = block.ConnectedBlockIds.Where(id => id != block.NormalNextBlockId && id != block.ReverseNextBlockId).ToList();
        return others.Count == 1 ? others[0] : null;
    }

    /// <summary>The switch position that connects switch block <paramref name="block"/> to <paramref name="legId"/>; null when it is not a leg.</summary>
    public static SwitchPosition? LegPosition(TrackBlock block, string legId)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (!block.HasSwitch) return null;
        if (legId == block.NormalNextBlockId) return SwitchPosition.Normal;
        if (legId == block.ReverseNextBlockId) return SwitchPosition.Reverse;
        return null;
    }

    /// <summary>
    /// Why a train cannot physically cross between adjacent blocks <paramref name="a"/> and <paramref name="b"/>
    /// right now (a switch between them is set to its other leg); null when it can.
    /// </summary>
    public static string? BoundaryAlignmentProblem(TrackBlock a, TrackBlock b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        foreach (var (sw, other) in new[] { (a, b), (b, a) })
        {
            if (LegPosition(sw, other.Id) is { } required && sw.Switch != required)
            {
                return $"switch {sw.Id} is {sw.Switch}, but crossing between {sw.Id} and {other.Id} needs it {required}";
            }
        }

        return null;
    }

    /// <summary>
    /// Checks that <paramref name="route"/> is a physical path on <paramref name="lineId"/>: every block exists
    /// on that line, consecutive blocks are connected, the path never reverses into the block it just left, and
    /// it never goes leg-to-leg through a switch. <paramref name="enteredFrom"/> is the block the train was in
    /// before <c>route[0]</c>, when known. Returns the problem, or null for a valid route.
    /// </summary>
    public static string? ValidateRoute(Func<string, TrackBlock?> find, string lineId, IReadOnlyList<string> route, string? enteredFrom = null)
    {
        ArgumentNullException.ThrowIfNull(find);
        if (route is null || route.Count == 0) return "The route has no blocks.";
        TrackBlock? previous = string.IsNullOrEmpty(enteredFrom) ? null : find(enteredFrom);
        for (int i = 0; i < route.Count; i++)
        {
            string id = route[i];
            if (string.IsNullOrWhiteSpace(id)) return $"Route block {i + 1} is blank.";
            var block = find(id);
            if (block is null) return $"Route block '{id}' is not in the track layout.";
            if (block.LineId != lineId) return $"Route block {id} is on the {block.LineId} Line, not the {lineId} Line.";
            if (previous is not null && !previous.ConnectedBlockIds.Contains(id))
                return $"The route goes from {previous.Id} to {id}, which are not connected.";
            if (i + 1 < route.Count)
            {
                string next = route[i + 1];
                if (previous is not null && next == previous.Id) return $"The route reverses at {id} (back into {previous.Id}).";
                if (block.HasSwitch && previous is not null && LegPosition(block, previous.Id) is not null && LegPosition(block, next) is not null)
                    return $"The route goes from {previous.Id} to {next} through switch {id}, which joins only its stem to a leg.";
            }

            previous = block;
        }

        return null;
    }

    /// <summary>
    /// The block the front of a train in <paramref name="current"/> physically enters next, given the block it
    /// entered <paramref name="current"/> from (<paramref name="previousId"/>; null when the train was placed in
    /// <paramref name="current"/>) and the block the route intends (<paramref name="intendedNextId"/>, used only
    /// where the track itself does not decide, i.e. the direction a placed train starts in). A switch decides by
    /// its CURRENT position for a train coming from its stem; a train coming from a leg always continues to the
    /// stem. Returns null with a reason when the track ends or the next block is ambiguous.
    /// </summary>
    public static (string? NextId, string? Problem) PhysicalNext(TrackBlock current, string? previousId, string? intendedNextId)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.HasSwitch)
        {
            string? stem = Stem(current);
            string aligned = current.Switch == SwitchPosition.Reverse ? current.ReverseNextBlockId : current.NormalNextBlockId;
            if (string.IsNullOrEmpty(previousId))
            {
                // Placed in the switch block: the route says which way it faces.
                if (intendedNextId is not null && intendedNextId == stem) return (stem, null);
                if (intendedNextId is not null && LegPosition(current, intendedNextId) is not null) return (aligned, null);
                return (null, $"the direction of a train placed in switch block {current.Id} is unknown");
            }

            if (previousId == stem) return (aligned, null);
            if (LegPosition(current, previousId) is not null)
                return stem is null ? (null, $"switch block {current.Id} has no stem") : (stem, null);
            return (null, $"block {previousId} is not connected to switch block {current.Id}");
        }

        var onward = current.ConnectedBlockIds.Where(id => id != previousId).Distinct().ToList();
        if (string.IsNullOrEmpty(previousId))
        {
            return intendedNextId is not null && onward.Contains(intendedNextId)
                ? (intendedNextId, null)
                : (null, $"the direction of a train placed in block {current.Id} is unknown");
        }

        // A junction without a switch cannot be resolved physically; the route is never trusted instead.
        return onward.Count switch
        {
            0 => (null, $"the track ends after block {current.Id}"),
            1 => (onward[0], null),
            _ => (null, $"block {current.Id} has several onward connections and no switch decides between them"),
        };
    }
}
