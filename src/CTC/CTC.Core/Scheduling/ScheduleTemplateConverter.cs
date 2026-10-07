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
/// time is therefore its departure time. A blank cell means the train does not use that
/// block, so a train on a branching line simply leaves the other branch blank. Physical
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

        // Every nonblank cell is a block on this train's route; blank cells are blocks it does not use.
        var scheduled = new Dictionary<string, (ScheduleTemplateRow Row, TimeSpan Time)>();
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

            scheduled[row.BlockId] = (row, time);
        }

        // Rows are in layout order, not travel order, so the route is recovered by walking the
        // topology from the start block: each step must go to a CONNECTED block that has a time.
        var route = new List<(ScheduleTemplateRow Row, TimeSpan Time)> { scheduled[startRow.BlockId] };
        var visited = new HashSet<string> { startRow.BlockId };
        while (true)
        {
            var current = route[^1];
            var nextIds = current.Row.ConnectedBlockIds
                .Where(id => !visited.Contains(id) && scheduled.ContainsKey(id))
                .Distinct()
                .ToList();

            if (nextIds.Count == 0)
            {
                break;
            }

            if (nextIds.Count > 1)
            {
                return $"{trainId} has times at both {nextIds[0]} and {nextIds[1]} after {current.Row.BlockId}. "
                    + "A train can follow only one branch; leave the other blank.";
            }

            var next = scheduled[nextIds[0]];

            // Crossing a block takes time, so the next block must be entered strictly later
            // (a zero-length block may be entered and left at the same second).
            bool tooEarly = current.Row.LengthMeters > 0 ? next.Time <= current.Time : next.Time < current.Time;
            if (tooEarly)
            {
                return $"{trainId} enters {next.Row.BlockId} at {Format(next.Time)}, which is not later than it enters "
                    + $"{current.Row.BlockId} at {Format(current.Time)}. Times must increase along the route.";
            }

            route.Add(next);
            visited.Add(next.Row.BlockId);
        }

        // Anything not reached is not connected to the route (a gap, a jump, or the other branch).
        var stray = template.Rows.FirstOrDefault(row => scheduled.ContainsKey(row.BlockId) && !visited.Contains(row.BlockId));
        if (stray is not null)
        {
            return $"{trainId} has a time at {stray.BlockId}, which is not connected to its route ending at {route[^1].Row.BlockId}. "
                + "Consecutive scheduled blocks must be connected.";
        }

        if (route.Count < 2)
        {
            return $"{trainId} has only a route start time. Enter the time it enters at least the next block.";
        }

        var scheduledTrain = new ScheduledTrain
        {
            TrainId = trainId,
            LineId = template.LineId,
        };

        foreach (var (row, time) in route)
        {
            scheduledTrain.BlockTimes.Add(new ScheduledBlockTime { BlockId = row.BlockId, ArrivalTime = time });
        }

        train = scheduledTrain;
        return null;
    }

    private static string Format(TimeSpan time) => time.ToString(TimeSpanFormat, CultureInfo.InvariantCulture);
}
