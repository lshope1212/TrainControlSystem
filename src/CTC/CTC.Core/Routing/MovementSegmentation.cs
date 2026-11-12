using CTC.Core.Models;
using TrainControl.Contracts.Enums;

namespace CTC.Core.Routing;

/// <summary>One passage of a route through a switch block.</summary>
/// <param name="RouteIndex">Position of the switch block in the route.</param>
/// <param name="RequiredPosition">The position the passage needs; null when it only touches the stem.</param>
public sealed record SwitchPassage(int RouteIndex, string SwitchBlockId, SwitchPosition? RequiredPosition);

/// <summary>
/// Splits a train's journey into movement-authority segments that the Track Controller can protect one at a time.
/// </summary>
/// <remarks>
/// <para>
/// A train's planned journey (<see cref="DispatchedTrainState.ActiveRouteBlockIds"/>) may visit a block more than
/// once, e.g. the Green Line's yard-to-Edgebrook route passes N (GREEN-77..85) once each way around the loop.
/// A single movement request may not: the Track Controller refuses a request that revisits a block, and it derives
/// switch demands from every live request, so one request containing both passages of switch 85 would demand
/// Normal and Reverse at once. CTC therefore covers the journey with bounded segments. Each segment is a
/// contiguous part of the route that:
/// </para>
/// <list type="number">
///   <item>contains no block twice (so every switch is passed at most once in it), and</item>
///   <item>never reaches into a switch's protection zone again after leaving it, when the two passages need
///   different positions: the switch can only be moved while its zone is clear and no granted authority covers it,
///   so the segment that set it one way must end before the train comes back to need it the other way, and</item>
///   <item>never ends inside a protection zone short of a switch the next segment needs: a train held at the end
///   of its segment there would occupy the zone and lock the very switch it waits for.</item>
/// </list>
/// <para>
/// The segment's end is the train's fixed authority end (<see cref="DispatchedTrainState.AuthorityLimitRouteIndex"/>):
/// successors at block transitions re-express it and never pass it. The movement continues into the next segment
/// only through an explicit new request (see <see cref="Services.CTCService.SynchronizeMovementRequestsAsync"/>),
/// issued once the train has left the protection zones of every switch the new segment must move, so the Track
/// Controller can set them before granting.
/// </para>
/// <para>
/// A switch's protection zone is taken from the layout as the switch block and the blocks it connects to
/// (<see cref="LineTopology.ProtectionZone"/>), which is how the course deployments configure it. The Track
/// Controller remains the final authority: CTC's segmentation only avoids asking for something it cannot grant.
/// </para>
/// </remarks>
public static class MovementSegmentation
{
    /// <summary>
    /// The last route index of the authority segment that starts at <paramref name="start"/>, at most
    /// <paramref name="limit"/>.
    /// </summary>
    /// <param name="enteredFromBlockId">The block before <c>route[0]</c>, when known (decides a switch passage at index 0).</param>
    public static int SegmentEnd(LineTopology topology, IReadOnlyList<string> route, int start, int limit, string? enteredFromBlockId = null)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(route);

        limit = Math.Min(limit, route.Count - 1);
        if (start < 0 || start >= route.Count || limit <= start)
        {
            return limit;
        }

        return SegmentEnd(new ZoneIndex(topology, route, enteredFromBlockId), route, start, limit);
    }

    /// <summary>The switch passages of <c>route[from..to]</c> (inclusive) and the position each needs.</summary>
    /// <param name="enteredFromBlockId">The block before <c>route[0]</c>, when known.</param>
    public static IReadOnlyList<SwitchPassage> SwitchPassages(LineTopology topology, IReadOnlyList<string> route, int from, int to, string? enteredFromBlockId = null)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(route);

        var passages = new List<SwitchPassage>();
        for (int i = Math.Max(0, from); i <= Math.Min(to, route.Count - 1); i++)
        {
            if (topology.IsSwitch(route[i]))
            {
                string? entry = i > 0 ? route[i - 1] : enteredFromBlockId;
                string? exit = i + 1 < route.Count ? route[i + 1] : null;
                passages.Add(new SwitchPassage(i, route[i], topology.RequiredSwitchPosition(entry, route[i], exit)));
            }
        }

        return passages;
    }

    /// <summary>
    /// Why <paramref name="train"/> cannot continue into route blocks <c>(fromIndex .. toIndex]</c> yet: a switch there
    /// must move (CTC's view of it is not the needed position) while a block of its protection zone is not
    /// clear, e.g. the train's own rear is still in it. Null when nothing stands in the way.
    /// </summary>
    public static string? ExtensionBlocker(CtcLineState line, LineTopology topology, IReadOnlyList<string> route, int fromIndex, int toIndex, string? enteredFromBlockId = null)
    {
        ArgumentNullException.ThrowIfNull(line);

        var blocks = line.Blocks.ToDictionary(block => block.BlockId, StringComparer.Ordinal);
        foreach (var passage in SwitchPassages(topology, route, fromIndex + 1, toIndex, enteredFromBlockId))
        {
            if (passage.RequiredPosition is not { } needed
                || (blocks.TryGetValue(passage.SwitchBlockId, out var switchBlock) && switchBlock.Switch == needed && !switchBlock.IsStatusStale))
            {
                continue;
            }

            foreach (string id in topology.ProtectionZone(passage.SwitchBlockId))
            {
                if (!blocks.TryGetValue(id, out var zone) || zone.Occupancy != OccupancyState.Clear || zone.IsStatusStale)
                {
                    return $"switch {passage.SwitchBlockId} must be set {needed}, and block {id} in its protection zone is not yet clear";
                }
            }
        }

        return null;
    }

    private static int SegmentEnd(ZoneIndex zones, IReadOnlyList<string> route, int start, int limit)
    {
        int end = RawSegmentEnd(zones, route, start, limit);
        return end < limit ? PullBackOutOfZones(zones, route, start, end, limit) : end;
    }

    /// <summary>Rules 1 and 2: extend from <paramref name="start"/> while no block repeats and no switch would need two positions.</summary>
    private static int RawSegmentEnd(ZoneIndex zones, IReadOnlyList<string> route, int start, int limit)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { route[start] };
        var claimed = new Dictionary<string, (int RunStart, SwitchPosition? Position)>(StringComparer.Ordinal);
        foreach (string switchId in zones.SwitchesProtectedBy(route[start]))
        {
            var run = zones.Run(switchId, start);
            claimed[switchId] = (run.Start, zones.RunRequirement(switchId, run));
        }

        int end = start;
        for (int k = start + 1; k <= limit; k++)
        {
            if (!seen.Add(route[k]))
            {
                break;
            }

            bool conflict = false;
            var updates = new List<(string SwitchId, int RunStart, SwitchPosition? Position)>();
            foreach (string switchId in zones.SwitchesProtectedBy(route[k]))
            {
                var run = zones.Run(switchId, k);
                var position = zones.RunRequirement(switchId, run);
                if (claimed.TryGetValue(switchId, out var earlier) && earlier.RunStart != run.Start)
                {
                    // The train comes back into this switch's zone. Fine if both passages need the same position (or
                    // one needs none); otherwise the switch must move in between, which this segment cannot allow.
                    if (earlier.Position is { } first && position is { } second && first != second)
                    {
                        conflict = true;
                        break;
                    }

                    if (earlier.Position is null)
                    {
                        updates.Add((switchId, run.Start, position));
                    }
                }
                else if (!claimed.ContainsKey(switchId))
                {
                    updates.Add((switchId, run.Start, position));
                }
            }

            if (conflict)
            {
                break;
            }

            foreach (var (switchId, runStart, position) in updates)
            {
                claimed[switchId] = (runStart, position);
            }

            end = k;
        }

        return end;
    }

    /// <summary>
    /// Rule 3: a segment followed by another must not end inside a protection zone short of the switch passage
    /// that zone protects. The end moves back to just before that zone, unless that would leave the segment only
    /// the train's own block, or the train, held there, could never continue (the next segment, started from the
    /// new end, would get no further). The start strictly increases with each check, so this always terminates.
    /// </summary>
    private static int PullBackOutOfZones(ZoneIndex zones, IReadOnlyList<string> route, int start, int end, int limit)
    {
        bool moved = true;
        while (moved)
        {
            moved = false;
            foreach (string switchId in zones.SwitchesProtectedBy(route[end]))
            {
                var run = zones.Run(switchId, end);
                if (run.End <= end || zones.RunPassage(switchId, run) is not { } passage || passage <= end || zones.RunRequirement(switchId, run) is null)
                {
                    continue;
                }

                int earlier = run.Start - 1;
                if (earlier > start && SegmentEnd(zones, route, earlier, limit) > earlier)
                {
                    end = earlier;
                    moved = true;
                    break;
                }
            }
        }

        return end;
    }

    /// <summary>Which route indices lie in which switch's protection zone.</summary>
    private sealed class ZoneIndex
    {
        private readonly LineTopology _topology;
        private readonly IReadOnlyList<string> _route;
        private readonly string? _enteredFrom;
        private readonly Dictionary<string, HashSet<string>> _zones = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _protectedBy = new(StringComparer.Ordinal);

        public ZoneIndex(LineTopology topology, IReadOnlyList<string> route, string? enteredFrom)
        {
            _topology = topology;
            _route = route;
            _enteredFrom = enteredFrom;
            foreach (string blockId in route.Distinct())
            {
                foreach (string candidate in topology.Neighbours(blockId).Prepend(blockId).Distinct())
                {
                    if (topology.IsSwitch(candidate) && !_zones.ContainsKey(candidate))
                    {
                        _zones[candidate] = [.. topology.ProtectionZone(candidate)];
                    }
                }
            }

            foreach (var (switchId, zone) in _zones)
            {
                foreach (string blockId in zone)
                {
                    if (!_protectedBy.TryGetValue(blockId, out var list))
                    {
                        _protectedBy[blockId] = list = [];
                    }

                    list.Add(switchId);
                }
            }
        }

        public IReadOnlyList<string> SwitchesProtectedBy(string blockId) => _protectedBy.TryGetValue(blockId, out var list) ? list : [];

        /// <summary>The maximal run of consecutive route indices around <paramref name="index"/> inside the switch's zone.</summary>
        public (int Start, int End) Run(string switchId, int index)
        {
            var zone = _zones[switchId];
            int start = index, end = index;
            while (start > 0 && zone.Contains(_route[start - 1]))
            {
                start--;
            }

            while (end + 1 < _route.Count && zone.Contains(_route[end + 1]))
            {
                end++;
            }

            return (start, end);
        }

        /// <summary>Where the run passes the switch block itself; null when it only touches the zone's other blocks.</summary>
        public int? RunPassage(string switchId, (int Start, int End) run)
        {
            for (int i = run.Start; i <= run.End; i++)
            {
                if (_route[i] == switchId)
                {
                    return i;
                }
            }

            return null;
        }

        /// <summary>The position the run's passage of the switch needs; null when it needs none.</summary>
        public SwitchPosition? RunRequirement(string switchId, (int Start, int End) run)
        {
            if (RunPassage(switchId, run) is not { } i)
            {
                return null;
            }

            string? entry = i > 0 ? _route[i - 1] : _enteredFrom;
            string? exit = i + 1 < _route.Count ? _route[i + 1] : null;
            return _topology.RequiredSwitchPosition(entry, switchId, exit);
        }
    }
}
