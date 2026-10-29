using System.Globalization;
using CTC.Core.Models;

namespace CTC.Core.Scheduling;

/// <summary>
/// The single place that decides how long a train dwells at a stop by default, and how
/// dwell times are entered and validated. The Schedule Builder, the CSV import, the schedule
/// converter and the Active Movement Plan all use it, so the rule is never repeated in the UI.
/// </summary>
/// <remarks>
/// Dwell is the time a train intentionally stays in a stop block after ENTERING it, so
/// <c>Departure = Arrival + Dwell</c>. Zero means the train does not intentionally stop.
/// A block is a station when the track layout gives it a station name
/// (<see cref="CtcBlockState.StationName"/>); block numbers are never used to decide.
/// </remarks>
public static class DwellTimePolicy
{
    /// <summary>Default dwell at a block that has a station.</summary>
    public static readonly TimeSpan StationDwellTime = TimeSpan.FromSeconds(45);

    /// <summary>Default dwell at a block without a station: the train does not stop.</summary>
    public static readonly TimeSpan NonStationDwellTime = TimeSpan.Zero;

    /// <summary>Default dwell for a block with the given station name (empty when it has no station).</summary>
    public static TimeSpan GetDefaultDwellTime(string? stationName) =>
        string.IsNullOrWhiteSpace(stationName) ? NonStationDwellTime : StationDwellTime;

    /// <summary>Default dwell for <paramref name="block"/>: 45 s at a station, otherwise 0 s.</summary>
    public static TimeSpan GetDefaultDwellTime(CtcBlockState block)
    {
        ArgumentNullException.ThrowIfNull(block);

        return GetDefaultDwellTime(block.StationName);
    }

    /// <summary>
    /// Default dwell for the block with <paramref name="blockId"/> on <paramref name="line"/>;
    /// 0 s when the block is not on the line.
    /// </summary>
    public static TimeSpan GetDefaultDwellTime(CtcLineState line, string? blockId)
    {
        ArgumentNullException.ThrowIfNull(line);

        var block = line.Blocks.FirstOrDefault(block => block.BlockId == blockId);
        return block is null ? NonStationDwellTime : GetDefaultDwellTime(block);
    }

    /// <summary>Dispatcher-readable reason <paramref name="dwellTime"/> is not allowed, or null when it is.</summary>
    public static string? Validate(TimeSpan dwellTime) =>
        dwellTime < TimeSpan.Zero ? "Dwell time cannot be negative." : null;

    /// <summary>
    /// Parses dwell text entered in whole seconds (e.g. "45"). Blank text is not a value;
    /// callers decide whether blank means "use the default".
    /// </summary>
    /// <returns>A dispatcher-readable error, or null with <paramref name="dwellTime"/> set.</returns>
    public static string? TryParseSeconds(string? text, out TimeSpan dwellTime)
    {
        dwellTime = TimeSpan.Zero;
        string trimmed = text?.Trim() ?? string.Empty;

        if (!int.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int seconds))
        {
            return $"Dwell time '{trimmed}' is not valid. Enter whole seconds, e.g. 45.";
        }

        if (seconds < 0)
        {
            return Validate(TimeSpan.FromSeconds(seconds));
        }

        dwellTime = TimeSpan.FromSeconds(seconds);
        return null;
    }

    /// <summary>Dwell as whole seconds text, the form the dispatcher enters it in.</summary>
    public static string FormatSeconds(TimeSpan dwellTime) =>
        ((long)Math.Round(dwellTime.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
}
