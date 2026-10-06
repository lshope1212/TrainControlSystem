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

        if (!TryParseTime(startText, out var departureTime))
        {
            return $"{trainId} has an invalid route start time '{startText.Trim()}'. Use {TimeFormat}.";
        }

        var scheduled = new ScheduledTrain
        {
            TrainId = trainId,
            LineId = template.LineId,
            StartBlockId = startRow.BlockId,
            DepartureTime = departureTime,
        };

        var previousTime = departureTime;
        foreach (var row in template.Rows.Where(row => row.IsStation && row != startRow))
        {
            string text = row.TrainTimes[column];

            // A blank station cell means this train does not stop there (e.g. another branch).
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (!TryParseTime(text, out var arrivalTime))
            {
                return $"{trainId} has an invalid arrival time at {row.StationName}. Use {TimeFormat}.";
            }

            if (arrivalTime < departureTime)
            {
                return $"{trainId} has an arrival time at {row.StationName} earlier than its departure time.";
            }

            if (arrivalTime < previousTime)
            {
                return $"{trainId} has an arrival time at {row.StationName} earlier than its previous scheduled stop.";
            }

            scheduled.Stops.Add(new ScheduleStop
            {
                BlockId = row.BlockId,
                StationName = row.StationName,
                ArrivalTime = arrivalTime,
            });
            previousTime = arrivalTime;
        }

        if (scheduled.Stops.Count == 0)
        {
            return $"{trainId} has no scheduled station stops.";
        }

        train = scheduled;
        return null;
    }
}
