using TrainControl.Common.Identity;

namespace CTC.Core.Models;

/// <summary>
/// Train ID rules. A train ID is the three-digit number only ("001", "002", ...); the
/// "Train " prefix is presentation text added by <see cref="DisplayName"/> and is never stored.
/// </summary>
/// <remarks>
/// CTC schedules only the ten trains of the dispatchable fleet, "001" to "010"
/// (<see cref="TrainIdConvention.IsDispatchable"/>), the trains the Train Controller controls.
/// IDs are unique across the whole system: a train on one line never shares its ID with a train
/// on another line.
/// </remarks>
public static class TrainIds
{
    /// <summary>Number of the first train CTC can schedule, "001".</summary>
    public const int FirstNumber = TrainIdConvention.FirstDispatchableNumber;

    /// <summary>Number of the last train CTC can schedule, "010".</summary>
    public const int LastNumber = TrainIdConvention.LastDispatchableNumber;

    /// <summary>
    /// Formats a train number as its ID, e.g. 1 -> "001", 10 -> "010". This is the system-wide
    /// canonical train ID (<see cref="TrainIdConvention"/>), used verbatim in every message about
    /// the train. General-purpose formatting: the number is not checked against the fleet.
    /// </summary>
    public static string Format(int number) => TrainIdConvention.Format(number);

    /// <summary>Dispatcher-facing label for a train ID, e.g. "001" -> "Train 001".</summary>
    public static string DisplayName(string trainId) => $"Train {trainId}";

    /// <summary>True when CTC can schedule and dispatch <paramref name="trainId"/> ("001" to "010").</summary>
    public static bool IsSupported(string? trainId) => TrainIdConvention.IsDispatchable(trainId);

    /// <summary>Dispatcher-readable reason <paramref name="trainId"/> cannot be scheduled.</summary>
    public static string UnsupportedMessage(string? trainId) =>
        $"{DisplayName(trainId ?? string.Empty)} is not a supported train. CTC can schedule only "
        + $"{DisplayName(TrainIdConvention.DispatchableTrainIds[0])} to {DisplayName(TrainIdConvention.DispatchableTrainIds[^1])}, "
        + "the trains the Train Controller controls.";

    /// <summary>
    /// Every train ID CTC currently knows about: scheduled, queued or dispatched, on any line.
    /// </summary>
    public static IReadOnlySet<string> KnownIds(CtcSystemState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state.ScheduledTrains.Select(train => train.TrainId)
            .Concat(state.DispatchQueue.Select(entry => entry.TrainId))
            .Concat(state.DispatchedTrains.Select(train => train.TrainId))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The supported train IDs no scheduled, queued or dispatched train uses, on any line,
    /// lowest first. A new schedule takes its IDs from here, so it never reuses a known ID
    /// whichever line it is for. Template IDs that were never queued are not tracked, so they
    /// may be handed out again.
    /// </summary>
    public static IReadOnlyList<string> AvailableIds(CtcSystemState state)
    {
        var known = KnownIds(state);
        return TrainIdConvention.DispatchableTrainIds.Where(id => !known.Contains(id)).ToArray();
    }

    /// <summary>
    /// The lowest <paramref name="count"/> available train IDs (see <see cref="AvailableIds"/>).
    /// On a fresh system the first train is "001" and later ones follow in order.
    /// </summary>
    /// <exception cref="InvalidOperationException">Fewer than <paramref name="count"/> supported IDs are free; the message is dispatcher-readable.</exception>
    public static IReadOnlyList<string> Allocate(CtcSystemState state, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        return Take(AvailableIds(state), count);
    }

    /// <summary>The first <paramref name="count"/> of <paramref name="availableIds"/>.</summary>
    /// <exception cref="InvalidOperationException">Fewer than <paramref name="count"/> IDs are available; the message is dispatcher-readable.</exception>
    internal static IReadOnlyList<string> Take(IReadOnlyList<string> availableIds, int count)
    {
        if (count > availableIds.Count)
        {
            throw new InvalidOperationException(NotEnoughIdsMessage(count, availableIds.Count));
        }

        return availableIds.Take(count).ToArray();
    }

    /// <summary>Dispatcher-readable explanation that <paramref name="requested"/> trains do not fit in the fleet.</summary>
    public static string NotEnoughIdsMessage(int requested, int available)
    {
        string fleet = $"the {TrainIdConvention.DispatchableTrainCount} supported trains "
            + $"({DisplayName(TrainIdConvention.DispatchableTrainIds[0])} to {DisplayName(TrainIdConvention.DispatchableTrainIds[^1])})";
        return available <= 0
            ? $"No train IDs are free: all of {fleet} are already scheduled, queued or dispatched."
            : $"{requested} train(s) were requested, but only {available} train ID(s) of {fleet} "
                + $"{(available == 1 ? "is" : "are")} free.";
    }
}
