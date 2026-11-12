using CTC.Core.Models;
using CTC.Core.Routing;

namespace CTC.Core.Dispatching;

/// <summary>
/// Fixed-block route finding over a line's track layout, used when the dispatcher re-plans
/// a train that has already been released (see <see cref="Services.CTCService.UpdateDispatchedTrainPlanAsync"/>).
/// </summary>
/// <remarks>
/// The shortest LEGAL route by block visits (<see cref="RoutePlanner"/>): it follows the line's direction of
/// travel, passes switches only between stem and leg, never reverses, and may revisit a block when the network
/// requires it. Switch positions and other trains are not considered here; authority (see
/// <see cref="AuthorityManager"/>) and the Track Controller keep the train out of unsafe blocks along the route.
/// </remarks>
public static class RouteManager
{
    /// <summary>
    /// Shortest legal route on <paramref name="line"/> from <paramref name="startBlockId"/> to
    /// <paramref name="destinationBlockId"/>, never leaving the line.
    /// </summary>
    /// <param name="enteredFromBlockId">
    /// The block the train entered the start block from (its direction of travel); null or empty when it starts there.
    /// </param>
    /// <returns>
    /// The route's blocks in travel order, starting with the start block and ending with the
    /// destination (just the start block when they are the same), or null when no legal route exists.
    /// </returns>
    /// <exception cref="ArgumentException">Either block is not on <paramref name="line"/>.</exception>
    public static IReadOnlyList<CtcBlockState>? FindRoute(CtcLineState line, string startBlockId, string destinationBlockId, string? enteredFromBlockId = null)
    {
        ArgumentNullException.ThrowIfNull(line);

        var blocksById = line.Blocks.ToDictionary(block => block.BlockId);
        if (!blocksById.ContainsKey(startBlockId))
        {
            throw new ArgumentException($"Block '{startBlockId}' is not on {line.Name}.", nameof(startBlockId));
        }

        if (!blocksById.ContainsKey(destinationBlockId))
        {
            throw new ArgumentException($"Block '{destinationBlockId}' is not on {line.Name}.", nameof(destinationBlockId));
        }

        var route = RoutePlanner.FindRoute(LineTopology.From(line), startBlockId, destinationBlockId, string.IsNullOrEmpty(enteredFromBlockId) ? null : enteredFromBlockId);
        return route?.Select(id => blocksById[id]).ToList();
    }
}
