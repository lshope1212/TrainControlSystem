using CTC.Core.Models;

namespace CTC.Core.Dispatching;

/// <summary>
/// Fixed-block route finding over a line's track layout, used when the dispatcher re-plans
/// a train that has already been released (see <see cref="Services.CTCService.UpdateDispatchedTrainPlanAsync"/>).
/// </summary>
/// <remarks>
/// Deliberately simple: the shortest path by block count over
/// <see cref="CtcBlockState.ConnectedBlockIds"/>. Direction of travel, switch positions and
/// other trains are not considered here; authority (see <see cref="AuthorityManager"/>)
/// is what keeps the train out of unsafe blocks along the route.
/// </remarks>
public static class RouteManager
{
    /// <summary>
    /// Shortest path (fewest blocks) on <paramref name="line"/> from
    /// <paramref name="startBlockId"/> to <paramref name="destinationBlockId"/>, following
    /// connected blocks and never leaving the line. Each block is visited at most once, so
    /// loops in the layout cannot cause an infinite search.
    /// </summary>
    /// <returns>
    /// The route's blocks in travel order, starting with the start block and ending with the
    /// destination (just the start block when they are the same), or null when no path exists.
    /// </returns>
    /// <exception cref="ArgumentException">Either block is not on <paramref name="line"/>.</exception>
    public static IReadOnlyList<CtcBlockState>? FindRoute(CtcLineState line, string startBlockId, string destinationBlockId)
    {
        ArgumentNullException.ThrowIfNull(line);

        var blocksById = line.Blocks.ToDictionary(block => block.BlockId);
        if (!blocksById.TryGetValue(startBlockId, out var start))
        {
            throw new ArgumentException($"Block '{startBlockId}' is not on {line.Name}.", nameof(startBlockId));
        }

        if (!blocksById.ContainsKey(destinationBlockId))
        {
            throw new ArgumentException($"Block '{destinationBlockId}' is not on {line.Name}.", nameof(destinationBlockId));
        }

        var previous = new Dictionary<string, CtcBlockState> { [start.BlockId] = start };
        var frontier = new Queue<CtcBlockState>();
        frontier.Enqueue(start);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            if (current.BlockId == destinationBlockId)
            {
                var route = new List<CtcBlockState>();
                for (var block = current; block != start; block = previous[block.BlockId])
                {
                    route.Add(block);
                }

                route.Add(start);
                route.Reverse();
                return route;
            }

            // Connections to blocks not on this line are ignored, so a route never crosses lines.
            foreach (var nextId in current.ConnectedBlockIds)
            {
                if (blocksById.TryGetValue(nextId, out var next) && previous.TryAdd(nextId, current))
                {
                    frontier.Enqueue(next);
                }
            }
        }

        return null;
    }
}
