namespace CTC.Core.Routing;

/// <summary>
/// Topology-aware route finding: the shortest route a train can legally drive from one block to another,
/// honouring one-way track, switch geometry and the no-reversal rule (see <see cref="LineTopology"/>).
/// </summary>
/// <remarks>
/// <para>
/// The search is a breadth-first search over <em>routing states</em> <c>(PreviousBlockId, CurrentBlockId)</c>,
/// not over blocks. Which way a train may continue depends on where it came from (a switch entered from a leg
/// leads only to its stem; a train never turns back), so the same physical block reached from a different
/// approach is a different state. That lets a route revisit a block when the network requires it (e.g. the
/// Green Line's N section, passed once each way around the loop through O-Q), while every state is expanded at
/// most once, so loops can never cause an endless search. The state space is finite (one state per directed
/// connection), so the search always terminates.
/// </para>
/// <para>
/// Every state transition costs one block visit, so the first time the destination is reached is a route
/// with the fewest block visits among all legal routes. When the start block's approach is unknown (a train
/// starting at a yard entry), any first step allowed by the topology is considered.
/// </para>
/// </remarks>
public static class RoutePlanner
{
    /// <summary>
    /// The shortest legal route from <paramref name="startBlockId"/> to <paramref name="destinationBlockId"/>.
    /// </summary>
    /// <param name="enteredFromBlockId">
    /// The block the train entered the start block from, which decides the direction it can continue in;
    /// null (or a block not connected to the start) when it starts there, e.g. leaving the yard.
    /// </param>
    /// <returns>
    /// The route's blocks in travel order, from the start to the destination (just the start block when they
    /// are the same), or null when no legal route exists.
    /// </returns>
    public static IReadOnlyList<string>? FindRoute(LineTopology topology, string startBlockId, string destinationBlockId, string? enteredFromBlockId = null)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(startBlockId);
        ArgumentNullException.ThrowIfNull(destinationBlockId);

        if (!topology.Contains(startBlockId) || !topology.Contains(destinationBlockId))
        {
            return null;
        }

        if (startBlockId == destinationBlockId)
        {
            return [startBlockId];
        }

        string? entry = enteredFromBlockId is not null && topology.Neighbours(startBlockId).Contains(enteredFromBlockId) ? enteredFromBlockId : null;
        var start = new RoutingState(entry, startBlockId);
        var parents = new Dictionary<RoutingState, RoutingState?> { [start] = null };
        var frontier = new Queue<RoutingState>();
        frontier.Enqueue(start);

        while (frontier.Count > 0)
        {
            var state = frontier.Dequeue();
            foreach (string next in topology.Neighbours(state.CurrentBlockId))
            {
                if (!topology.CanStep(state.PreviousBlockId, state.CurrentBlockId, next))
                {
                    continue;
                }

                var reached = new RoutingState(state.CurrentBlockId, next);
                if (!parents.TryAdd(reached, state))
                {
                    continue;
                }

                if (next == destinationBlockId)
                {
                    return Reconstruct(parents, reached);
                }

                frontier.Enqueue(reached);
            }
        }

        return null;
    }

    private static List<string> Reconstruct(Dictionary<RoutingState, RoutingState?> parents, RoutingState end)
    {
        var route = new List<string>();
        for (RoutingState? state = end; state is { } current; state = parents[current])
        {
            route.Add(current.CurrentBlockId);
        }

        route.Reverse();
        return route;
    }

    /// <summary>A train in <see cref="CurrentBlockId"/> that entered it from <see cref="PreviousBlockId"/> (null: it started there).</summary>
    private readonly record struct RoutingState(string? PreviousBlockId, string CurrentBlockId);
}
