using CTC.Core.Models;

namespace CTC.Core.Scheduling;

/// <summary>
/// CTC's route-start policy: a train enters service only from one of its line's yard entries, the
/// blocks the Track Model's layout marks <see cref="CtcBlockState.IsYardEntry"/>. CTC never infers
/// an entry (from the lowest block number, a switch or a station); a line whose layout states
/// none cannot be scheduled until the layout does. Used by the Schedule Builder, the CSV import,
/// <see cref="ScheduleTemplateConverter"/> and <see cref="Services.CTCService.QueueSchedule"/>, so
/// every way of scheduling a train applies the same rule.
/// </summary>
public static class YardEntries
{
    /// <summary>The line's yard-entry blocks, in layout order; empty when its layout states none.</summary>
    public static IReadOnlyList<CtcBlockState> For(CtcLineState line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return line.Blocks.Where(block => block.IsYardEntry).ToList();
    }

    /// <summary>Dispatcher-readable label of a start block, e.g. "K63 (GREEN-63)", or "A1" when the label is the ID.</summary>
    public static string Label(CtcBlockState block)
    {
        ArgumentNullException.ThrowIfNull(block);

        string label = $"{block.Section}{block.BlockNumber}";
        return label == block.BlockId ? label : $"{label} ({block.BlockId})";
    }

    /// <summary>Why <paramref name="line"/> cannot be scheduled: its layout has no yard entries.</summary>
    public static string NoneConfiguredMessage(CtcLineState line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return $"{DisplayName(line)} has no yard-entry blocks in its track layout, so CTC cannot start a train's route on it. "
            + "The Track Model's layout must mark the block(s) where trains leave the yard (IsYardEntry) before trains can be scheduled on this line.";
    }

    /// <summary>
    /// Checks that <paramref name="startBlockId"/> is a permitted route start for a train on
    /// <paramref name="line"/>. Returns a dispatcher-readable reason it is not, or null when it is.
    /// </summary>
    /// <param name="trainName">Display name used in the message, e.g. "Train 001".</param>
    /// <param name="allLines">Every line CTC knows, so a block of another line is reported as such.</param>
    public static string? Validate(CtcLineState line, string? startBlockId, string trainName, IEnumerable<CtcLineState>? allLines = null)
    {
        ArgumentNullException.ThrowIfNull(line);

        var entries = For(line);
        if (entries.Count == 0)
        {
            return $"{trainName} cannot be scheduled: {NoneConfiguredMessage(line)}";
        }

        string choices = string.Join(", ", entries.Select(Label));
        if (string.IsNullOrWhiteSpace(startBlockId))
        {
            return $"{trainName} has no starting block. Choose one of {DisplayName(line)}'s yard entries: {choices}.";
        }

        var block = line.Blocks.FirstOrDefault(block => block.BlockId == startBlockId);
        if (block is null)
        {
            var otherLine = allLines?.FirstOrDefault(other => other != line && other.Blocks.Any(b => b.BlockId == startBlockId));
            return otherLine is null
                ? $"{trainName}'s starting block {startBlockId} is not on {DisplayName(line)}. Choose one of its yard entries: {choices}."
                : $"{trainName}'s starting block {startBlockId} is on {DisplayName(otherLine)}, not {DisplayName(line)}. Choose one of {DisplayName(line)}'s yard entries: {choices}.";
        }

        if (!block.IsYardEntry)
        {
            return $"{trainName} cannot start at {Label(block)}: it is not a yard entry. Trains enter {DisplayName(line)} from the yard only at {choices}.";
        }

        return null;
    }

    private static string DisplayName(CtcLineState line) => string.IsNullOrWhiteSpace(line.Name) ? $"Line {line.LineId}" : line.Name;
}
