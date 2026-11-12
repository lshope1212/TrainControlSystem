using System.Globalization;
using CTC.Core.Models;
using CTC.Core.Routing;

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
/// A train's time in a block row is the time it ENTERS that block, except in the train's own
/// starting block (one of the line's yard entries, chosen per train), where it is the departure
/// time. The route always begins at that starting block; no block before it is ever added. The
/// dispatcher only times the blocks that matter (e.g. stations); the timed blocks are taken in time order and CTC routes the train
/// between them along the shortest LEGAL path (<see cref="RoutePlanner"/>: the line's direction of travel,
/// switches only between stem and leg, no reversing), continuing in the direction the train arrived at
/// each waypoint. A block may be visited more than once when the network requires it (e.g. the Green
/// Line's yard-to-Edgebrook route passes N twice, once each way around the loop), so a single timed
/// destination is enough. Blank blocks not on that path are not used, so on a branching line timing any
/// block on a branch selects that branch. Physical feasibility (speed) is checked later by CTC when the
/// schedule is queued, because it needs the live track layout.
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

        if (template.YardEntryBlockIds.Count == 0)
        {
            return ScheduleConversionResult.Failure(
                $"{template.LineName} has no yard-entry blocks in its track layout, so CTC cannot start a train's route on it. "
                + "The Track Model's layout must mark the block(s) where trains leave the yard before trains can be scheduled on this line.");
        }

        var trains = new List<ScheduledTrain>();
        for (int column = 0; column < template.TrainIds.Count; column++)
        {
            ScheduledTrain? train = null;
            string? error = TryGetStartRow(template, column, out var startRow)
                ?? TryConvertTrain(template, startRow!, column, out train);
            if (error is not null)
            {
                return ScheduleConversionResult.Failure(error);
            }

            trains.Add(train!);
        }

        return ScheduleConversionResult.Success(trains);
    }

    /// <summary>The train's selected starting block, which must be one of the template's yard entries.</summary>
    private static string? TryGetStartRow(ScheduleTemplate template, int column, out ScheduleTemplateRow? startRow)
    {
        string trainName = TrainIds.DisplayName(template.TrainIds[column]);
        string startId = template.GetStartBlockId(column);
        startRow = template.GetStartRow(column);

        if (startId.Length == 0 || startRow is null)
        {
            return $"{trainName} has no starting block. Choose one of {template.LineName}'s yard entries: {template.DescribeYardEntries()}.";
        }

        if (!template.YardEntryBlockIds.Contains(startId))
        {
            return $"{trainName} cannot start at {ScheduleTemplate.Label(startRow)}: it is not a yard entry. Trains enter {template.LineName} from the yard only at {template.DescribeYardEntries()}.";
        }

        if (template.IsDepartureUnconfirmed(column))
        {
            return $"{trainName}'s time at its starting block {ScheduleTemplate.Label(startRow)} was entered as an arrival time before that block became its start. "
                + "Re-enter it to confirm it as the departure time.";
        }

        return null;
    }

    /// <summary>Parses one schedule time cell. Blank text is not a valid time.</summary>
    public static bool TryParseTime(string? text, out TimeSpan time) =>
        TimeSpan.TryParseExact(text?.Trim(), TimeSpanFormat, CultureInfo.InvariantCulture, out time);

    /// <summary>Returns an error message, or null with <paramref name="train"/> set.</summary>
    private static string? TryConvertTrain(ScheduleTemplate template, ScheduleTemplateRow startRow, int column, out ScheduledTrain? train)
    {
        train = null;
        string trainId = template.TrainIds[column];
        string trainName = TrainIds.DisplayName(trainId);

        // DepartureTime is not entered separately: it is the train's time at its own starting block.
        string startLabel = ScheduleTemplate.Label(startRow);
        string startText = startRow.TrainTimes[column];
        if (string.IsNullOrWhiteSpace(startText))
        {
            return $"{trainName} is missing its departure time at its starting block {startLabel}.";
        }

        if (!TryParseTime(startText, out _))
        {
            return $"{trainName} has an invalid departure time '{startText.Trim()}' at its starting block {startLabel}. Use {TimeFormat}.";
        }

        // Every nonblank cell is a timed waypoint. Blank cells are either blocks the train
        // passes through between waypoints (CTC fills those in below) or blocks it does not use.
        var waypoints = new List<(ScheduleTemplateRow Row, TimeSpan Time, TimeSpan Dwell)>();
        foreach (var row in template.Rows)
        {
            string text = row.TrainTimes[column];
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (!TryParseTime(text, out var time))
            {
                return $"{trainName} has an invalid time '{text.Trim()}' at {row.BlockId}. Use {TimeFormat}.";
            }

            string? dwellError = TryGetDwellTime(row, column, row == startRow, out var dwell);
            if (dwellError is not null)
            {
                return $"{trainName} at {row.BlockId}: {dwellError}";
            }

            waypoints.Add((row, time, dwell));
        }

        var start = waypoints.Single(waypoint => waypoint.Row == startRow);
        var stops = waypoints.Where(waypoint => waypoint.Row != startRow).OrderBy(waypoint => waypoint.Time).ToList();
        if (stops.Count == 0)
        {
            return $"{trainName} has only a departure time. Enter the time it enters at least one more block after {startLabel}.";
        }

        // Rows are in layout order, not travel order; travel order is time order.
        if (stops[0].Time <= start.Time)
        {
            return $"{trainName} is scheduled to enter {stops[0].Row.BlockId} at {Format(stops[0].Time)}, "
                + $"which is not later than its departure from {start.Row.BlockId} at {Format(start.Time)}.";
        }

        for (int i = 1; i < stops.Count; i++)
        {
            if (stops[i].Time == stops[i - 1].Time)
            {
                return $"{trainName} is scheduled to enter both {stops[i - 1].Row.BlockId} and {stops[i].Row.BlockId} "
                    + $"at {Format(stops[i].Time)}. Times must increase along the route.";
            }
        }

        // Connect each waypoint to the next along the line's legal topology, routing through blocks
        // that have no time and continuing in the direction the train arrived in. Blocks may repeat
        // where the network requires it (a loop); a train never reverses.
        var topology = LineTopology.From(template.Rows);
        var timeByBlockId = waypoints.ToDictionary(waypoint => waypoint.Row.BlockId, waypoint => waypoint.Time);
        var route = new List<ScheduledRouteBlock> { new() { BlockId = start.Row.BlockId, ArrivalTime = start.Time } };

        foreach (var stop in stops)
        {
            string fromId = route[^1].BlockId;
            string? enteredFrom = route.Count > 1 ? route[^2].BlockId : null;
            var path = RoutePlanner.FindRoute(topology, fromId, stop.Row.BlockId, enteredFrom)?.Skip(1).ToList();
            if (path is null || path.Count == 0)
            {
                // "Connected" here means legally driveable: the direction of travel and switch geometry apply.
                return $"{trainName} cannot reach {stop.Row.BlockId} from {fromId} along connected blocks without reversing. "
                    + "Check the times are in travel order and on one branch.";
            }

            // The path passes a block the dispatcher timed for LATER, so the times are out of order.
            var passed = path.SkipLast(1).FirstOrDefault(blockId => timeByBlockId.TryGetValue(blockId, out var time) && time > stop.Time);
            if (passed is not null)
            {
                return $"{trainName} passes {passed} on the way from {fromId} to {stop.Row.BlockId}, but is scheduled to enter "
                    + $"{passed} later, at {Format(timeByBlockId[passed])}. Times must increase along the route.";
            }

            foreach (var blockId in path.SkipLast(1))
            {
                route.Add(new ScheduledRouteBlock { BlockId = blockId });
            }

            // Dwell does not change when the train enters the stop; it delays the next movement,
            // which SpeedPlanner.ValidateFeasibility checks once the schedule is queued.
            route.Add(new ScheduledRouteBlock { BlockId = stop.Row.BlockId, ArrivalTime = stop.Time, DwellTime = stop.Dwell });
        }

        // The route is built from the selected start, so this only guards that construction.
        if (route[0].BlockId != startRow.BlockId)
        {
            return $"{trainName}'s route does not begin at its starting block {startLabel}.";
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
    /// The dwell for one timed cell: blank means the block's default (45 s at a station,
    /// otherwise 0 s; see <see cref="DwellTimePolicy"/>), so schedules without dwell times keep
    /// working. The train's starting block never dwells. Returns an error message, or null.
    /// </summary>
    private static string? TryGetDwellTime(ScheduleTemplateRow row, int column, bool isStart, out TimeSpan dwell)
    {
        dwell = TimeSpan.Zero;
        string text = row.TrainDwellTimes[column];

        if (string.IsNullOrWhiteSpace(text))
        {
            dwell = isStart ? TimeSpan.Zero : row.DefaultDwellTime;
            return null;
        }

        string? error = DwellTimePolicy.TryParseSeconds(text, out dwell);
        if (error is not null)
        {
            return error;
        }

        if (isStart && dwell > TimeSpan.Zero)
        {
            return "the starting block has no dwell time; trains depart it at their departure time.";
        }

        return null;
    }

    private static string Format(TimeSpan time) => time.ToString(TimeSpanFormat, CultureInfo.InvariantCulture);
}
