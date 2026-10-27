using System.Globalization;

namespace CTC.Core.Models;

/// <summary>
/// Train ID rules. A train ID is the three-digit number only ("000", "001", ...); the
/// "Train " prefix is presentation text added by <see cref="DisplayName"/> and is never stored.
/// </summary>
public static class TrainIds
{
    /// <summary>Highest number a three-digit train ID can hold.</summary>
    public const int MaxNumber = 999;

    /// <summary>Formats a zero-based train number as its ID, e.g. 0 -> "000", 10 -> "010".</summary>
    public static string Format(int number)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(number);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, MaxNumber);

        return number.ToString("D3", CultureInfo.InvariantCulture);
    }

    /// <summary>Dispatcher-facing label for a train ID, e.g. "000" -> "Train 000".</summary>
    public static string DisplayName(string trainId) => $"Train {trainId}";

    /// <summary>
    /// The number after the highest train ID CTC currently knows about (scheduled, queued or
    /// dispatched), or 0 if there is none, so a new schedule never reuses a known ID.
    /// IDs not in the three-digit format are ignored. Template IDs that were never queued
    /// are not tracked, so they may be handed out again.
    /// </summary>
    public static int NextAvailableNumber(CtcSystemState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var knownIds = state.ScheduledTrains.Select(train => train.TrainId)
            .Concat(state.DispatchQueue.Select(entry => entry.TrainId))
            .Concat(state.DispatchedTrains.Select(train => train.TrainId));

        int highest = -1;
        foreach (var trainId in knownIds)
        {
            if (trainId.Length == 3
                && int.TryParse(trainId, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                && number > highest)
            {
                highest = number;
            }
        }

        return highest + 1;
    }
}
