using CTC.Core.Models;

namespace CTC.Core.Scheduling;

/// <summary>
/// Builds blank <see cref="ScheduleTemplate"/>s from a line's track layout, so the
/// layout received from the Track Model stays the single source of block data.
/// </summary>
public static class ScheduleTemplateFactory
{
    /// <summary>
    /// Creates a template with one row per block of <paramref name="line"/> and
    /// <paramref name="trainCount"/> blank train columns, with consecutive train IDs starting
    /// at <paramref name="firstTrainNumber"/> ("001" by default). The Schedule Builder uses
    /// <see cref="Create(CtcLineState, IReadOnlyList{string})"/> with
    /// <see cref="TrainIds.Allocate"/> instead, which also skips IDs CTC already uses.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="firstTrainNumber"/> is below <see cref="TrainIds.FirstNumber"/>.</exception>
    /// <exception cref="InvalidOperationException">The train IDs would go past <see cref="TrainIds.LastNumber"/>.</exception>
    public static ScheduleTemplate Create(CtcLineState line, int trainCount, int firstTrainNumber = TrainIds.FirstNumber)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(trainCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(firstTrainNumber, TrainIds.FirstNumber);

        // Only the supported fleet can be scheduled, so the IDs end at "010".
        var trainIds = SupportedIdsFrom(firstTrainNumber);
        if (trainCount > trainIds.Count)
        {
            throw new InvalidOperationException(
                $"Only {trainIds.Count} more train ID(s) are available; supported train IDs end at {TrainIds.Format(TrainIds.LastNumber)}.");
        }

        return Create(line, trainIds.Take(trainCount).ToArray());
    }

    /// <summary>
    /// Creates a template with one row per block of <paramref name="line"/> and one blank train
    /// column per entry of <paramref name="trainIds"/> (normally from <see cref="TrainIds.Allocate"/>).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="trainIds"/> is empty, repeats an ID, or contains an ID outside the
    /// supported fleet ("001" to "010"). The message is dispatcher-readable (no parameter name).
    /// </exception>
    public static ScheduleTemplate Create(CtcLineState line, IReadOnlyList<string> trainIds)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(trainIds);

        // No parameter names below, so the messages stay dispatcher-readable as they are.
        if (trainIds.Count == 0)
        {
            throw new ArgumentException("A schedule template needs at least one train.");
        }

        var unsupported = trainIds.FirstOrDefault(id => !TrainIds.IsSupported(id));
        if (unsupported is not null || trainIds.Any(id => id is null))
        {
            throw new ArgumentException(TrainIds.UnsupportedMessage(unsupported));
        }

        if (trainIds.Distinct(StringComparer.Ordinal).Count() != trainIds.Count)
        {
            throw new ArgumentException("A schedule template cannot use the same train ID twice.");
        }

        int trainCount = trainIds.Count;
        var startBlock = GetRouteStartBlock(line);

        var rows = line.Blocks.Select(block => new ScheduleTemplateRow(trainCount)
        {
            LineId = line.LineId,
            Section = block.Section,
            BlockId = block.BlockId,
            BlockNumber = block.BlockNumber,
            StationName = block.StationName,
            Infrastructure = DescribeInfrastructure(block),
            LengthMeters = block.LengthMeters,
            SpeedLimitKilometersPerHour = block.SpeedLimitKilometersPerHour,
            ConnectedBlockIds = block.ConnectedBlockIds.ToList(),
            IsRouteStart = block == startBlock,
            DefaultDwellTime = DwellTimePolicy.GetDefaultDwellTime(block),
        });

        return new ScheduleTemplate(line.LineId, trainIds, rows);
    }

    /// <summary>Supported train IDs from <paramref name="firstTrainNumber"/> to "010", in order.</summary>
    internal static IReadOnlyList<string> SupportedIdsFrom(int firstTrainNumber) =>
        firstTrainNumber > TrainIds.LastNumber
            ? []
            : Enumerable.Range(firstTrainNumber, TrainIds.LastNumber - firstTrainNumber + 1).Select(TrainIds.Format).ToArray();

    /// <summary>
    /// The block every train on <paramref name="line"/> starts its route from.
    /// ASSUMPTION: the lowest-numbered block. Replace this once routing/yard logic
    /// exists; nothing else should rely on that rule.
    /// </summary>
    public static CtcBlockState? GetRouteStartBlock(CtcLineState line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return line.Blocks.MinBy(block => block.BlockNumber);
    }

    /// <summary>Readable summary of a block's features, e.g. "Station B; Signal".</summary>
    public static string DescribeInfrastructure(CtcBlockState block)
    {
        ArgumentNullException.ThrowIfNull(block);

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(block.StationName))
        {
            parts.Add(block.StationName);
        }

        if (block.HasSwitch)
        {
            parts.Add("Switch");
        }

        if (block.HasSignal)
        {
            parts.Add("Signal");
        }

        if (block.HasCrossing)
        {
            parts.Add("Railway Crossing");
        }

        return string.Join("; ", parts);
    }
}
