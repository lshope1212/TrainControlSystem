using CTC.Core.Models;
using CTC.Core.Routing;

namespace CTC.Core.Scheduling;

/// <summary>
/// Reports where a scheduled route conflicts with the line's direction of travel or switch geometry, as given
/// by the Track Model's layout (the rules of <see cref="LineTopology"/>, which the Track Controller enforces for
/// movement authority). Routes CTC plans itself (<see cref="ScheduleTemplateConverter"/>,
/// <see cref="Dispatching.RouteManager"/>) already follow these rules, so this finds problems only in a route
/// given some other way; it does not change or reject any route, it makes the problem visible to the dispatcher.
/// </summary>
public static class RouteGeometry
{
    /// <summary>Dispatcher-readable problems with <paramref name="train"/>'s route; empty when none are found.</summary>
    public static IReadOnlyList<string> FindProblems(CtcLineState line, ScheduledTrain train)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(train);

        var topology = LineTopology.From(line);
        var route = train.Route.Select(step => step.BlockId).ToList();
        if (route.Any(id => !topology.Contains(id)))
        {
            return Array.Empty<string>();
        }

        string trainName = TrainIds.DisplayName(train.TrainId);
        var problems = new List<string>();

        // Against the direction of travel: report the first such step, and how many there are.
        var against = new List<(string From, string To)>();
        for (int i = 0; i + 1 < route.Count; i++)
        {
            if (topology.IsAgainstDirection(route[i], route[i + 1]))
            {
                against.Add((route[i], route[i + 1]));
            }
        }

        if (against.Count > 0)
        {
            var (from, to) = against[0];
            problems.Add($"{trainName}'s route moves {from} → {to} against {line.Name}'s direction of travel"
                + (against.Count > 1 ? $" ({against.Count} such steps)" : string.Empty)
                + "; the Track Controller will refuse that movement. Time a block on the route in the direction of travel, or choose another starting block.");
        }

        // Through a switch from one leg to the other, never via its stem: no switch position connects them.
        for (int i = 1; i + 1 < route.Count; i++)
        {
            if (topology.StepProblem(route[i - 1], route[i], route[i + 1]) == RouteStepProblem.LegToLeg)
            {
                problems.Add($"{trainName}'s route passes switch {route[i]} from {route[i - 1]} to {route[i + 1]}, two legs of the switch; "
                    + $"a train can only pass between its stem ({topology.Stem(route[i])}) and one leg, so the Track Controller cannot authorize it.");
            }
        }

        // Straight back into the block it just left.
        for (int i = 1; i + 1 < route.Count; i++)
        {
            if (route[i - 1] == route[i + 1])
            {
                problems.Add($"{trainName}'s route reverses at {route[i]} (back into {route[i - 1]}); a train cannot change direction on the line.");
            }
        }

        return problems;
    }
}
