using TrackController.Contracts.Messages;

namespace TrackController.TestUI.Wpf.ViewModels;

/// <summary>
/// Names the blocks the test presets act on, derived from the topology the controller reports.
/// The first listed exit of a switch is its normal branch, matching the shared map files.
/// </summary>
internal sealed record ScenarioPlan(string Target, string[] Path, string SwitchId, string NormalExit, string[] MismatchOccupied, string? SignalId, string? CrossingId, string LastBlockId)
{
    /// <summary>Returns null when the layout has no switch with an approach block, so no preset applies.</summary>
    public static ScenarioPlan? From(IReadOnlyList<WaysideBlockStatus> blocks)
    {
        var junction = blocks.FirstOrDefault(block => block.HasSwitch && block.NextBlockIds.Count >= 2);
        var approach = junction is null ? null : blocks.FirstOrDefault(block => block.NextBlockIds.Contains(junction.BlockId));
        if (junction is null || approach is null)
        {
            return null;
        }

        // Snapshots group blocks by territory, so choose equipment and the last block by numeric block order.
        var ordered = blocks.OrderBy(block => block.BlockId.Length).ThenBy(block => block.BlockId, StringComparer.Ordinal).ToArray();
        string? After(string id) => blocks.FirstOrDefault(block => block.BlockId == id)?.NextBlockIds.FirstOrDefault();
        string normal = junction.NextBlockIds[0], reverse = junction.NextBlockIds[1];
        string?[] path = [junction.BlockId, normal, After(normal)];
        string?[] mismatch = [blocks.FirstOrDefault(block => block.NextBlockIds.Contains(approach.BlockId))?.BlockId, reverse, After(reverse)];
        return new(approach.BlockId, [.. path.OfType<string>()], junction.BlockId, normal, [.. mismatch.OfType<string>()], ordered.FirstOrDefault(block => block.HasSignal)?.BlockId, ordered.FirstOrDefault(block => block.HasCrossing)?.BlockId, ordered[^1].BlockId);
    }
}
