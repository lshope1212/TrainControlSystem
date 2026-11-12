using CTC.Core.Models;
using CTC.Core.Scheduling;
using TrainControl.Contracts.Enums;

namespace CTC.Core.Routing;

/// <summary>One block of a line as the route planner sees it: the static layout data that decides where a train may go next.</summary>
/// <param name="TravelDirection">"Forward" (toward increasing numbers), "Reverse" (decreasing) or "Bidirectional", as the Track Model's layout gives it.</param>
/// <param name="NormalNextBlockId">For a switch block: the leg its Normal position connects to; empty otherwise.</param>
/// <param name="ReverseNextBlockId">For a switch block: the leg its Reverse position connects to; empty otherwise.</param>
public sealed record TopologyBlock(
    string BlockId,
    int BlockNumber,
    string TravelDirection,
    IReadOnlyList<string> ConnectedBlockIds,
    string NormalNextBlockId,
    string ReverseNextBlockId)
{
    /// <summary>The layout names both legs, so the block's points join its stem to one leg at a time.</summary>
    public bool IsSwitch => NormalNextBlockId.Length > 0 && ReverseNextBlockId.Length > 0;
}

/// <summary>Why one step of a route is not a legal train movement.</summary>
public enum RouteStepProblem
{
    None = 0,

    /// <summary>The two blocks are not connected on this line.</summary>
    NotConnected,

    /// <summary>The train would go straight back into the block it just left.</summary>
    Reversal,

    /// <summary>The step runs against a one-way block's direction of travel.</summary>
    AgainstDirection,

    /// <summary>The step passes a switch from one leg to the other, never via its stem.</summary>
    LegToLeg,
}

/// <summary>
/// The directed track topology of one line, built from the Track Model's layout: which block a train may
/// move to next, given the block it is in and the block it entered that one from. Used by the route
/// planner (<see cref="RoutePlanner"/>), the route check (<see cref="RouteGeometry"/>) and movement
/// segmentation (<see cref="MovementSegmentation"/>), so all three apply exactly the rules the Track
/// Controller enforces for movement authority.
/// </summary>
/// <remarks>
/// <para>
/// <b>Direction of travel</b> follows the Track Model's convention: a one-way block (<c>Forward</c>: toward
/// increasing numbers, <c>Reverse</c>: decreasing) with two connections is left only toward its downstream
/// neighbour, the next number in its direction when connected, otherwise its other (switch-branch)
/// connection, and entered only from the opposite one. A bidirectional block may be passed either way.
/// </para>
/// <para>
/// <b>Switch geometry</b>: a switch block's stem is its connection that is neither its Normal nor its
/// Reverse leg. A train passes between the stem and one leg; going from one leg straight to the other is
/// not a path. Crossing between the switch block and a leg needs the switch set to that leg, in either
/// direction of travel (the same rule the Track Controller uses to derive a route's switch demands).
/// </para>
/// <para>
/// A train never reverses into the block it just left. Nothing here is specific to a line: the Blue, Red
/// and Green lines all follow from their layout data alone.
/// </para>
/// </remarks>
public sealed class LineTopology
{
    private readonly Dictionary<string, TopologyBlock> _blocks;
    private readonly Dictionary<string, (string? Upstream, string? Downstream)> _flow;

    public LineTopology(IEnumerable<TopologyBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        _blocks = new Dictionary<string, TopologyBlock>(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            _blocks.TryAdd(block.BlockId, block);
        }

        _flow = _blocks.Values.ToDictionary(block => block.BlockId, Flow, StringComparer.Ordinal);
    }

    public static LineTopology From(CtcLineState line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return new LineTopology(line.Blocks.Select(block => new TopologyBlock(
            block.BlockId, block.BlockNumber, block.TravelDirection, block.ConnectedBlockIds.ToList(), block.NormalNextBlockId, block.ReverseNextBlockId)));
    }

    public static LineTopology From(IEnumerable<ScheduleTemplateRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return new LineTopology(rows.Select(row => new TopologyBlock(
            row.BlockId, row.BlockNumber, row.TravelDirection, row.ConnectedBlockIds, row.NormalNextBlockId, row.ReverseNextBlockId)));
    }

    public bool Contains(string blockId) => _blocks.ContainsKey(blockId);

    public TopologyBlock? Find(string blockId) => _blocks.GetValueOrDefault(blockId);

    /// <summary>The blocks <paramref name="blockId"/> connects to on this line (connections off the line are ignored).</summary>
    public IEnumerable<string> Neighbours(string blockId) =>
        _blocks.TryGetValue(blockId, out var block) ? block.ConnectedBlockIds.Where(_blocks.ContainsKey).Distinct() : [];

    /// <summary>The one block a one-way block may be left toward; null for a bidirectional block.</summary>
    public string? Downstream(string blockId) => _flow.GetValueOrDefault(blockId).Downstream;

    /// <summary>The one block a one-way block may be entered from; null for a bidirectional block.</summary>
    public string? Upstream(string blockId) => _flow.GetValueOrDefault(blockId).Upstream;

    /// <summary>Whether <paramref name="blockId"/> is a switch block whose legs are known.</summary>
    public bool IsSwitch(string blockId) => _blocks.TryGetValue(blockId, out var block) && block.IsSwitch;

    /// <summary>The stem of switch block <paramref name="blockId"/>: its connection that is not a leg; null if it is not a switch.</summary>
    public string? Stem(string blockId)
    {
        if (!_blocks.TryGetValue(blockId, out var block) || !block.IsSwitch)
        {
            return null;
        }

        var stems = block.ConnectedBlockIds.Where(id => id != block.NormalNextBlockId && id != block.ReverseNextBlockId).Distinct().ToList();
        return stems.Count == 1 ? stems[0] : null;
    }

    /// <summary>The switch position that joins switch block <paramref name="switchBlockId"/> to <paramref name="legId"/>; null when it is not one of its legs.</summary>
    public SwitchPosition? LegPosition(string switchBlockId, string? legId)
    {
        if (legId is null || !_blocks.TryGetValue(switchBlockId, out var block) || !block.IsSwitch)
        {
            return null;
        }

        return legId == block.NormalNextBlockId ? SwitchPosition.Normal
            : legId == block.ReverseNextBlockId ? SwitchPosition.Reverse
            : null;
    }

    /// <summary>
    /// The position a train passing switch block <paramref name="switchBlockId"/> from <paramref name="entryBlockId"/>
    /// to <paramref name="exitBlockId"/> needs it in: the leg it enters from or leaves by. Null when the block is
    /// not a switch, the movement only touches the stem, or it goes leg to leg (not a path).
    /// </summary>
    public SwitchPosition? RequiredSwitchPosition(string? entryBlockId, string switchBlockId, string? exitBlockId)
    {
        var entry = LegPosition(switchBlockId, entryBlockId);
        var exit = LegPosition(switchBlockId, exitBlockId);
        return entry is not null && exit is not null ? null : entry ?? exit;
    }

    /// <summary>
    /// The blocks protecting switch <paramref name="switchBlockId"/>: the switch block and every block it connects
    /// to. The Track Controller may move a switch only while all of them are clear and unreserved; its course
    /// deployments configure exactly these blocks as each switch's protection zone.
    /// </summary>
    public IReadOnlyCollection<string> ProtectionZone(string switchBlockId) =>
        _blocks.ContainsKey(switchBlockId) ? Neighbours(switchBlockId).Prepend(switchBlockId).Distinct().ToList() : [];

    /// <summary>Whether a one-way rule forbids moving directly from <paramref name="fromId"/> to <paramref name="toId"/>.</summary>
    public bool IsAgainstDirection(string fromId, string toId) =>
        (Downstream(fromId) is { } exit && exit != toId) || (Upstream(toId) is { } entry && entry != fromId);

    /// <summary>
    /// Why a train in <paramref name="currentId"/>, having entered it from <paramref name="previousId"/> (null when it
    /// starts there), may not continue to <paramref name="nextId"/>; <see cref="RouteStepProblem.None"/> when it may.
    /// </summary>
    public RouteStepProblem StepProblem(string? previousId, string currentId, string nextId)
    {
        if (!_blocks.TryGetValue(currentId, out var current) || !_blocks.ContainsKey(nextId) || !current.ConnectedBlockIds.Contains(nextId))
        {
            return RouteStepProblem.NotConnected;
        }

        if (previousId is not null && previousId == nextId)
        {
            return RouteStepProblem.Reversal;
        }

        if (IsAgainstDirection(currentId, nextId))
        {
            return RouteStepProblem.AgainstDirection;
        }

        if (previousId is not null && current.IsSwitch && LegPosition(currentId, previousId) is not null && LegPosition(currentId, nextId) is not null)
        {
            return RouteStepProblem.LegToLeg;
        }

        return RouteStepProblem.None;
    }

    /// <summary>Whether a train in <paramref name="currentId"/>, entered from <paramref name="previousId"/>, may continue to <paramref name="nextId"/>.</summary>
    public bool CanStep(string? previousId, string currentId, string nextId) => StepProblem(previousId, currentId, nextId) == RouteStepProblem.None;

    /// <summary>
    /// The first illegal step of <paramref name="route"/> (entered from <paramref name="enteredFromBlockId"/> when known),
    /// as its index in the route (the step from <c>route[Index]</c> to <c>route[Index + 1]</c>) and the problem; null for a legal route.
    /// </summary>
    public (int Index, RouteStepProblem Problem)? FindIllegalStep(IReadOnlyList<string> route, string? enteredFromBlockId = null)
    {
        ArgumentNullException.ThrowIfNull(route);

        for (int i = 0; i + 1 < route.Count; i++)
        {
            string? previous = i > 0 ? route[i - 1] : enteredFromBlockId;
            var problem = StepProblem(previous, route[i], route[i + 1]);
            if (problem != RouteStepProblem.None)
            {
                return (i, problem);
            }
        }

        return null;
    }

    private (string? Upstream, string? Downstream) Flow(TopologyBlock block)
    {
        int step = block.TravelDirection switch { "Forward" => 1, "Reverse" => -1, _ => 0 };
        var connections = block.ConnectedBlockIds.Distinct().ToList();
        if (step == 0 || connections.Count != 2)
        {
            return (null, null);
        }

        string? Numbered(int number) => connections.FirstOrDefault(id => _blocks.TryGetValue(id, out var other) && other.BlockNumber == number);
        string? next = Numbered(block.BlockNumber + step);
        string? previous = Numbered(block.BlockNumber - step);
        string Other(string id) => connections.First(connected => connected != id);

        if (next is not null)
        {
            return (Other(next), next);
        }

        // A switch-branch end (e.g. Green 100 -> 85): the numbered neighbour is upstream.
        return previous is not null ? (previous, Other(previous)) : (null, null);
    }
}
