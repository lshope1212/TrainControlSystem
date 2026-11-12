using CTC.Core.Models;

namespace CTC.Core.Scheduling;

/// <summary>
/// Reports where a scheduled route conflicts with the line's direction of travel or switch
/// geometry, as given by the Track Model's layout. CTC's route planner
/// (<see cref="ScheduleTemplateConverter"/>) finds the shortest CONNECTED path and ignores both, so a
/// route can be accepted by CTC and then refused by the Track Controller, which permits only the
/// layout's direction of travel and moves through a switch only between its stem and one leg. This
/// check does not change or reject any route; it makes that limitation visible to the dispatcher.
/// </summary>
/// <remarks>
/// Direction follows the Track Model's convention: a one-way block (<c>Forward</c>: toward
/// increasing numbers, <c>Reverse</c>: decreasing) leaves only toward its downstream neighbour, the
/// next number in its direction when connected, otherwise its other (switch-branch) connection, and
/// is entered only from the opposite one. A switch block's stem is its connection that is neither
/// its Normal nor its Reverse block.
/// </remarks>
public static class RouteGeometry
{
    /// <summary>Dispatcher-readable problems with <paramref name="train"/>'s route; empty when none are found.</summary>
    public static IReadOnlyList<string> FindProblems(CtcLineState line, ScheduledTrain train)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(train);

        var blocks = line.Blocks.ToDictionary(block => block.BlockId, StringComparer.Ordinal);
        var route = train.Route.Select(step => blocks.GetValueOrDefault(step.BlockId)).ToList();
        if (route.Any(block => block is null))
        {
            return Array.Empty<string>();
        }

        string trainName = TrainIds.DisplayName(train.TrainId);
        var problems = new List<string>();

        // Against the direction of travel: report the first such step, and how many there are.
        var against = new List<(CtcBlockState From, CtcBlockState To)>();
        for (int i = 0; i + 1 < route.Count; i++)
        {
            if (!IsPermittedStep(route[i]!, route[i + 1]!, blocks))
            {
                against.Add((route[i]!, route[i + 1]!));
            }
        }

        if (against.Count > 0)
        {
            var (from, to) = against[0];
            problems.Add($"{trainName}'s route moves {from.BlockId} → {to.BlockId} against {line.Name}'s direction of travel"
                + (against.Count > 1 ? $" ({against.Count} such steps)" : string.Empty)
                + "; the Track Controller will refuse that movement. Time a block on the route in the direction of travel, or choose another starting block.");
        }

        // Through a switch from one leg to the other, never via its stem: no switch position connects them.
        for (int i = 1; i + 1 < route.Count; i++)
        {
            var block = route[i]!;
            string? stem = Stem(block);
            if (stem is not null && route[i - 1]!.BlockId != stem && route[i + 1]!.BlockId != stem)
            {
                problems.Add($"{trainName}'s route passes switch {block.BlockId} from {route[i - 1]!.BlockId} to {route[i + 1]!.BlockId}, two legs of the switch; "
                    + $"a train can only pass between its stem ({stem}) and one leg, so the Track Controller cannot authorize it.");
            }
        }

        return problems;
    }

    private static bool IsPermittedStep(CtcBlockState from, CtcBlockState to, IReadOnlyDictionary<string, CtcBlockState> blocks) =>
        (Downstream(from, blocks) is not { } exit || exit == to.BlockId)
        && (Upstream(to, blocks) is not { } entry || entry == from.BlockId);

    /// <summary>The one block a one-way block may be left toward; null for a bidirectional block.</summary>
    private static string? Downstream(CtcBlockState block, IReadOnlyDictionary<string, CtcBlockState> blocks) =>
        Flow(block, blocks).Downstream;

    /// <summary>The one block a one-way block may be entered from; null for a bidirectional block.</summary>
    private static string? Upstream(CtcBlockState block, IReadOnlyDictionary<string, CtcBlockState> blocks) =>
        Flow(block, blocks).Upstream;

    private static (string? Upstream, string? Downstream) Flow(CtcBlockState block, IReadOnlyDictionary<string, CtcBlockState> blocks)
    {
        int step = block.TravelDirection switch { "Forward" => 1, "Reverse" => -1, _ => 0 };
        if (step == 0 || block.ConnectedBlockIds.Count != 2)
        {
            return (null, null);
        }

        string? Numbered(int number) => block.ConnectedBlockIds.FirstOrDefault(id => blocks.TryGetValue(id, out var other) && other.BlockNumber == number);
        string? next = Numbered(block.BlockNumber + step);
        string? previous = Numbered(block.BlockNumber - step);
        string Other(string id) => block.ConnectedBlockIds.First(connected => connected != id);

        if (next is not null)
        {
            return (Other(next), next);
        }

        // A switch-branch end (e.g. Green 100 -> 85): the numbered neighbour is upstream.
        return previous is not null ? (previous, Other(previous)) : (null, null);
    }

    private static string? Stem(CtcBlockState block)
    {
        if (block.NormalNextBlockId.Length == 0 || block.ReverseNextBlockId.Length == 0)
        {
            return null;
        }

        var stems = block.ConnectedBlockIds.Where(id => id != block.NormalNextBlockId && id != block.ReverseNextBlockId).ToList();
        return stems.Count == 1 ? stems[0] : null;
    }
}
