using System.Globalization;
using CTC.Core.Models;

namespace CTC.Core.Scheduling;

/// <summary>Outcome of <see cref="ScheduleTemplateConverter.Convert"/>.</summary>
public sealed class ScheduleConversionResult
{
    private ScheduleConversionResult(IReadOnlyList<ScheduledTrain> trains, string? errorMessage)
    {
        Trains = trains;
        ErrorMessage = errorMessage;
    }

    public bool IsSuccess => ErrorMessage is null;

    /// <summary>Dispatcher-readable reason the schedule was rejected; null on success.</summary>
    public string? ErrorMessage { get; }

    /// <summary>One train per template column on success; empty on failure.</summary>
    public IReadOnlyList<ScheduledTrain> Trains { get; }

    internal static ScheduleConversionResult Success(IReadOnlyList<ScheduledTrain> trains) => new(trains, null);

    internal static ScheduleConversionResult Failure(string errorMessage) => new(Array.Empty<ScheduledTrain>(), errorMessage);
}

/// <summary>
/// Validates a <see cref="ScheduleTemplate"/> and converts each train column into one
/// <see cref="ScheduledTrain"/>. The whole template is rejected on the first error, so an
/// invalid schedule is never partially converted.
/// </summary>
/// <remarks>
/// A train's time in a block row is the time it ENTERS that block; the route-start row's
/// time is therefore its departure time. The dispatcher only times the blocks that matter
/// (e.g. stations); the timed blocks are taken in time order and CTC routes the train
/// between them through the connected blank blocks. Blank blocks not on that path are not
/// used, so on a branching line timing any block on a branch selects that branch. Physical
/// feasibility (speed) is checked later by CTC when the schedule is queued, because it
/// needs the live track layout.
/// </remarks>
public static class ScheduleTemplateConverter
{
    public const string TimeFormat = "HH:mm:ss";

    private const string TimeSpanFormat = @"hh\:mm\:ss";

    public static ScheduleConversionResult Convert(ScheduleTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (template.TrainIds.Count == 0)
        {
            return ScheduleConversionResult.Failure("The schedule has no trains.");
        }

        var startRow = template.Rows.FirstOrDefault(row => row.IsRouteStart);
        if (startRow is null)
        {
            return ScheduleConversionResult.Failure("The schedule has no route start block.");
        }

        var trains = new List<ScheduledTrain>();
        for (int column = 0; column < template.TrainIds.Count; column++)
        {
            string? error = TryConvertTrain(template, startRow, column, out var train);
            if (error is not null)
            {
                return ScheduleConversionResult.Failure(error);
            }

            trains.Add(train!);
        }

        return ScheduleConversionResult.Success(trains);
    }

    /// <summary>Parses one schedule time cell. Blank text is not a valid time.</summary>
    public static bool TryParseTime(string? text, out TimeSpan time) =>
        TimeSpan.TryParseExact(text?.Trim(), TimeSpanFormat, CultureInfo.InvariantCulture, out time);

    /// <summary>Returns an error message, or null with <paramref name="train"/> set.</summary>
    private static string? TryConvertTrain(ScheduleTemplate template, ScheduleTemplateRow startRow, int column, out ScheduledTrain? train)
    {
        train = null;
        string trainId = template.TrainIds[column];

        // DepartureTime is not entered separately: it is the train's time at the route-start block.
        string startText = startRow.TrainTimes[column];
        if (string.IsNullOrWhiteSpace(startText))
        {
            return $"{trainId} is missing a route start time.";
        }

        if (!TryParseTime(startText, out _))
        {
            return $"{trainId} has an invalid route start time '{startText.Trim()}'. Use {TimeFormat}.";
        }

        // Every nonblank cell is a timed waypoint. Blank cells are either blocks the train
        // passes through between waypoints (CTC fills those in below) or blocks it does not use.
        var waypoints = new List<(ScheduleTemplateRow Row, TimeSpan Time)>();
        foreach (var row in template.Rows)
        {
            string text = row.TrainTimes[column];
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (!TryParseTime(text, out var time))
            {
                return $"{trainId} has an invalid time '{text.Trim()}' at {row.BlockId}. Use {TimeFormat}.";
            }

            waypoints.Add((row, time));
        }

        var start = waypoints.Single(waypoint => waypoint.Row == startRow);
        var stops = waypoints.Where(waypoint => waypoint.Row != startRow).OrderBy(waypoint => waypoint.Time).ToList();
        if (stops.Count == 0)
        {
            return $"{trainId} has only a route start time. Enter the time it enters at least one more block.";
        }

        // Rows are in layout order, not travel order; travel order is time order.
        if (stops[0].Time <= start.Time)
        {
            return $"{trainId} is scheduled to enter {stops[0].Row.BlockId} at {Format(stops[0].Time)}, "
                + $"which is not later than its departure from {start.Row.BlockId} at {Format(start.Time)}.";
        }

        for (int i = 1; i < stops.Count; i++)
        {
            if (stops[i].Time == stops[i - 1].Time)
            {
                return $"{trainId} is scheduled to enter both {stops[i - 1].Row.BlockId} and {stops[i].Row.BlockId} "
                    + $"at {Format(stops[i].Time)}. Times must increase along the route.";
            }
        }

        // Connect each waypoint to the next through the track topology, routing through blocks
        // that have no time. A block is never used twice, so a train cannot reverse onto the
        // other branch of a switch.
        var rowsById = template.Rows.ToDictionary(row => row.BlockId);
        var timeByBlockId = waypoints.ToDictionary(waypoint => waypoint.Row.BlockId, waypoint => waypoint.Time);
        var route = new List<ScheduledRouteBlock> { new() { BlockId = start.Row.BlockId, ArrivalTime = start.Time } };
        var used = new HashSet<string> { start.Row.BlockId };

        foreach (var stop in stops)
        {
            string fromId = route[^1].BlockId;
            var path = FindPath(rowsById, fromId, stop.Row.BlockId, used);
            if (path is null)
            {
                return $"{trainId} cannot reach {stop.Row.BlockId} from {fromId} along connected blocks without reversing. "
                    + "Check the times are in travel order and on one branch.";
            }

            // The path passes a block the dispatcher timed for LATER, so the times are out of order.
            var passed = path.SkipLast(1).FirstOrDefault(timeByBlockId.ContainsKey);
            if (passed is not null)
            {
                return $"{trainId} passes {passed} on the way from {fromId} to {stop.Row.BlockId}, but is scheduled to enter "
                    + $"{passed} later, at {Format(timeByBlockId[passed])}. Times must increase along the route.";
            }

            foreach (var blockId in path.SkipLast(1))
            {
                route.Add(new ScheduledRouteBlock { BlockId = blockId });
                used.Add(blockId);
            }

            route.Add(new ScheduledRouteBlock { BlockId = stop.Row.BlockId, ArrivalTime = stop.Time });
            used.Add(stop.Row.BlockId);
        }

        var scheduledTrain = new ScheduledTrain
        {
            TrainId = trainId,
            LineId = template.LineId,
        };

        foreach (var block in route)
        {
            scheduledTrain.Route.Add(block);
        }

        train = scheduledTrain;
        return null;
    }

    /// <summary>
    /// Shortest path (fewest blocks) from <paramref name="fromId"/> to <paramref name="toId"/>
    /// over <see cref="ScheduleTemplateRow.ConnectedBlockIds"/>, never entering a block in
    /// <paramref name="used"/>. Returns the blocks after <paramref name="fromId"/>, ending with
    /// <paramref name="toId"/>, or null when there is no such path.
    /// </summary>
    /// <remarks>
    /// On a line with loops more than one path can exist; the dispatcher picks a specific one
    /// by timing a block on it.
    /// </remarks>
    private static List<string>? FindPath(IReadOnlyDictionary<string, ScheduleTemplateRow> rowsById, string fromId, string toId, IReadOnlySet<string> used)
    {
        var previous = new Dictionary<string, string> { [fromId] = fromId };
        var frontier = new Queue<string>();
        frontier.Enqueue(fromId);

        while (frontier.Count > 0)
        {
            string current = frontier.Dequeue();
            if (current == toId)
            {
                var path = new List<string>();
                for (string blockId = toId; blockId != fromId; blockId = previous[blockId])
                {
                    path.Add(blockId);
                }

                path.Reverse();
                return path;
            }

            if (!rowsById.TryGetValue(current, out var row))
            {
                continue;
            }

            foreach (var next in row.ConnectedBlockIds)
            {
                if (!used.Contains(next) && rowsById.ContainsKey(next) && previous.TryAdd(next, current))
                {
                    frontier.Enqueue(next);
                }
            }
        }

        return null;
    }

    private static string Format(TimeSpan time) => time.ToString(TimeSpanFormat, CultureInfo.InvariantCulture);
}
