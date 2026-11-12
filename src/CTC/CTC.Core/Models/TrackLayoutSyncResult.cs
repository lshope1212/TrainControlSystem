namespace CTC.Core.Models;

/// <summary>What CTC did with one received track layout, line by line.</summary>
public sealed class TrackLayoutSyncResult
{
    /// <summary>Lines CTC did not have before.</summary>
    public List<string> AddedLineIds { get; } = new List<string>();

    /// <summary>Same blocks and connections; only static attributes (names, lengths, limits, equipment flags) changed, in place.</summary>
    public List<string> UpdatedLineIds { get; } = new List<string>();

    /// <summary>Identical: nothing changed, so live status, schedules, the queue and train tracking were untouched.</summary>
    public List<string> UnchangedLineIds { get; } = new List<string>();

    /// <summary>The topology changed (blocks or connections). The line had no released trains, so it was rebuilt.</summary>
    public List<string> ReplacedLineIds { get; } = new List<string>();

    /// <summary>Lines no longer in the layout, which had no released trains, so they were removed.</summary>
    public List<string> RemovedLineIds { get; } = new List<string>();

    /// <summary>Incompatible changes CTC refused (the line keeps its previous topology), with the reasons.</summary>
    public List<string> RejectedChanges { get; } = new List<string>();

    /// <summary>Scheduled trains dropped because their route no longer exists in the new topology.</summary>
    public List<string> DroppedScheduledTrainIds { get; } = new List<string>();

    /// <summary>
    /// Scheduled trains not yet released that were dropped because their start block is no longer
    /// one of the line's yard entries in the new layout (an obsolete start selection). They are
    /// also listed in <see cref="DroppedScheduledTrainIds"/>.
    /// </summary>
    public List<string> InvalidStartTrainIds { get; } = new List<string>();

    /// <summary>True when any line was added, updated, replaced or removed.</summary>
    public bool Changed => AddedLineIds.Count + UpdatedLineIds.Count + ReplacedLineIds.Count + RemovedLineIds.Count > 0;

    /// <summary>Dispatcher-readable one-line summary.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            Add(parts, "added", AddedLineIds);
            Add(parts, "updated", UpdatedLineIds);
            Add(parts, "rebuilt (topology changed)", ReplacedLineIds);
            Add(parts, "removed", RemovedLineIds);
            Add(parts, "unchanged", UnchangedLineIds);
            string summary = parts.Count == 0 ? "Track layout received; no lines." : $"Track layout: {string.Join("; ", parts)}.";
            var routeGone = DroppedScheduledTrainIds.Except(InvalidStartTrainIds).ToList();
            if (routeGone.Count > 0)
            {
                summary += $" Schedules dropped because their route no longer exists: {string.Join(", ", routeGone.Select(TrainIds.DisplayName))}.";
            }

            if (InvalidStartTrainIds.Count > 0)
            {
                summary += $" Schedules dropped because their start block is no longer a yard entry: {string.Join(", ", InvalidStartTrainIds.Select(TrainIds.DisplayName))}; schedule them again from a current yard entry.";
            }

            if (RejectedChanges.Count > 0)
            {
                summary += " " + string.Join(" ", RejectedChanges);
            }

            return summary;
        }
    }

    private static void Add(List<string> parts, string verb, List<string> lineIds)
    {
        if (lineIds.Count > 0)
        {
            parts.Add($"{verb} {string.Join(", ", lineIds)}");
        }
    }
}
