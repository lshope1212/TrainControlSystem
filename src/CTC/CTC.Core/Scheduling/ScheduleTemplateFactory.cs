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
    /// <paramref name="trainCount"/> blank train columns.
    /// </summary>
    public static ScheduleTemplate Create(CtcLineState line, int trainCount)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(trainCount);

        var trainIds = Enumerable.Range(1, trainCount).Select(CreateTrainId);
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
        });

        return new ScheduleTemplate(line.LineId, trainIds, rows);
    }

    /// <summary>
    /// Train ID for the Nth train column (1-based). Placeholder until scheduled trains
    /// are mapped to real Train Model train IDs.
    /// </summary>
    public static string CreateTrainId(int trainNumber) => $"Train {trainNumber}";

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
