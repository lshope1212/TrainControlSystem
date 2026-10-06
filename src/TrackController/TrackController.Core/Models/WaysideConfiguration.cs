using TrackController.Contracts.Messages;

namespace TrackController.Core.Models;

/// <summary>Builds and validates topology while keeping caller-owned collections out of controller state.</summary>
public static class WaysideConfiguration
{
    /// <summary>Creates the illustrative twelve-block topology used by the standalone demonstration.</summary>
    public static WaysideConfigurationMessage CreateDemo()
    {
        var config = new WaysideConfigurationMessage();
        for (int i = 1; i <= 12; i++)
        {
            config.Blocks.Add(new WaysideBlockDefinition { BlockId = i.ToString(), LengthMeters = 100, SpeedLimitMetersPerSecond = i >= 10 ? 8 : 15, NextBlockIds = i is 9 or 12 ? [] : [i == 6 ? "7" : (i + 1).ToString()], HasSignal = i is 4 or 6 or 8, MapX = i <= 9 ? 30 + (i - 1) * 106 : 30 + (i - 4) * 106, MapY = i <= 9 ? 60 : 165 });
        }

        var points = config.Blocks[5];
        points.NextBlockIds.Add("10");
        points.NormalNextBlockId = "7";
        points.ReverseNextBlockId = "10";
        points.StemBlockId = "5";
        points.SwitchProtectionBlockIds = ["5", "6", "7", "10"];
        config.Blocks[7].HasCrossing = true;
        config.Blocks[7].CrossingProtectionBlockIds = ["7", "8", "9"];
        return config;
    }

    /// <summary>Rejects invalid topology or ownership and returns independent configuration collections.</summary>
    public static WaysideConfigurationMessage ValidateAndCopy(WaysideConfigurationMessage input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.WaysideId) || string.IsNullOrWhiteSpace(input.LineName) || input.Blocks is null || input.Blocks.Count is < 1 or > 500)
            throw new ArgumentException("Provide a controller, line, and 1â€“500 blocks.");
        if (!double.IsFinite(input.FeedbackTimeoutSeconds) || input.FeedbackTimeoutSeconds is < 1 or > 60 || !double.IsFinite(input.RequestTimeoutSeconds) || input.RequestTimeoutSeconds is < 1 or > 120)
            throw new ArgumentException("Feedback timeout must be 1â€“60 s; request timeout must be 1â€“120 s.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var blockDefinition in input.Blocks)
        {
            if (blockDefinition is null || string.IsNullOrWhiteSpace(blockDefinition.BlockId) || !ids.Add(blockDefinition.BlockId) || !double.IsFinite(blockDefinition.LengthMeters) || blockDefinition.LengthMeters <= 0 || !double.IsFinite(blockDefinition.SpeedLimitMetersPerSecond) || blockDefinition.SpeedLimitMetersPerSecond <= 0 || !double.IsFinite(blockDefinition.MapX) || !double.IsFinite(blockDefinition.MapY) || blockDefinition.MapX is < 0 or > 100000 || blockDefinition.MapY is < 0 or > 100000)
                throw new ArgumentException("Block IDs must be unique and dimensions, limits, and map coordinates valid.");
        }

        foreach (var blockDefinition in input.Blocks)
        {
            if (blockDefinition.NextBlockIds is null || blockDefinition.SwitchProtectionBlockIds is null || blockDefinition.CrossingProtectionBlockIds is null || blockDefinition.NextBlockIds.Concat(blockDefinition.SwitchProtectionBlockIds).Concat(blockDefinition.CrossingProtectionBlockIds).Any(id => !ids.Contains(id)) || blockDefinition.NextBlockIds.Contains(blockDefinition.BlockId) || blockDefinition.NextBlockIds.Distinct().Count() != blockDefinition.NextBlockIds.Count)
                throw new ArgumentException($"Block {blockDefinition.BlockId} has invalid connections or protection zones.");
            bool hasSwitch = blockDefinition.NormalNextBlockId is not null || blockDefinition.ReverseNextBlockId is not null;
            if (hasSwitch && (blockDefinition.NormalNextBlockId == blockDefinition.ReverseNextBlockId || blockDefinition.NormalNextBlockId is null || blockDefinition.ReverseNextBlockId is null || !blockDefinition.NextBlockIds.Contains(blockDefinition.NormalNextBlockId) || !blockDefinition.NextBlockIds.Contains(blockDefinition.ReverseNextBlockId) || !new[]
            {
                blockDefinition.BlockId,
                blockDefinition.NormalNextBlockId,
                blockDefinition.ReverseNextBlockId
            }.All(blockDefinition.SwitchProtectionBlockIds.Contains)))
                throw new ArgumentException($"Switch {blockDefinition.BlockId} needs two distinct connected exits and a complete protection zone.");
            if (blockDefinition.StemBlockId is not null && (!hasSwitch || !ids.Contains(blockDefinition.StemBlockId) || blockDefinition.StemBlockId == blockDefinition.BlockId || blockDefinition.StemBlockId == blockDefinition.NormalNextBlockId || blockDefinition.StemBlockId == blockDefinition.ReverseNextBlockId || !blockDefinition.SwitchProtectionBlockIds.Contains(blockDefinition.StemBlockId)))
                throw new ArgumentException($"Switch {blockDefinition.BlockId} has an invalid stem or incomplete protection zone.");
            if (blockDefinition.NextBlockIds.Count > 2 && !hasSwitch)
                throw new ArgumentException($"Branching block {blockDefinition.BlockId} must define its switch exits.");
            if (blockDefinition.HasCrossing && (!blockDefinition.CrossingProtectionBlockIds.Contains(blockDefinition.BlockId) || blockDefinition.CrossingProtectionBlockIds.Count < 2))
                throw new ArgumentException($"Crossing {blockDefinition.BlockId} needs its block and approach blocks in its protection zone.");
        }

        if (input.OwnedBlockIds is { } owned && (owned.Count == 0 || owned.Distinct().Count() != owned.Count || owned.Any(id => !ids.Contains(id))))
            throw new ArgumentException("Territory ownership must contain distinct blocks from the shared track map.");
        var owners = GetOwnedIds(input);
        foreach (var block in input.Blocks.Where(blockDefinition => owners.Contains(blockDefinition.BlockId)))
            if (block.SwitchProtectionBlockIds.Concat(block.CrossingProtectionBlockIds).Any(id => !owners.Contains(id)))
                throw new ArgumentException($"Keep block {block.BlockId} and its complete switch/crossing protection zone in one territory.");
        return new WaysideConfigurationMessage
        {
            WaysideId = input.WaysideId,
            LineName = input.LineName,
            FeedbackTimeoutSeconds = input.FeedbackTimeoutSeconds,
            RequestTimeoutSeconds = input.RequestTimeoutSeconds,
            OwnedBlockIds = input.OwnedBlockIds is null ? null : [.. input.OwnedBlockIds],
            Blocks = input.Blocks.Select(block => new WaysideBlockDefinition { BlockId = block.BlockId, LengthMeters = block.LengthMeters, SpeedLimitMetersPerSecond = block.SpeedLimitMetersPerSecond, NextBlockIds = [.. block.NextBlockIds], HasSignal = block.HasSignal, NormalNextBlockId = block.NormalNextBlockId, ReverseNextBlockId = block.ReverseNextBlockId, StemBlockId = block.StemBlockId, SwitchProtectionBlockIds = [.. block.SwitchProtectionBlockIds], HasCrossing = block.HasCrossing, CrossingProtectionBlockIds = [.. block.CrossingProtectionBlockIds], MapX = block.MapX, MapY = block.MapY }).ToList()
        };
    }

    /// <summary>Returns the blocks this controller may command, including legacy full-map ownership.</summary>
    public static HashSet<string> GetOwnedIds(WaysideConfigurationMessage configuration) => new(configuration.OwnedBlockIds ?? configuration.Blocks.Select(blockDefinition => blockDefinition.BlockId).ToList(), StringComparer.Ordinal);
    /// <summary>Finds adjacent foreign blocks whose feedback is needed for boundary observation.</summary>
    public static HashSet<string> GetNeighborIds(WaysideConfigurationMessage configuration)
    {
        var owned = GetOwnedIds(configuration);
        return configuration.Blocks.Where(blockDefinition => owned.Contains(blockDefinition.BlockId)).SelectMany(blockDefinition => blockDefinition.NextBlockIds).Concat(configuration.Blocks.Where(blockDefinition => blockDefinition.NextBlockIds.Any(owned.Contains)).Select(blockDefinition => blockDefinition.BlockId)).Where(id => !owned.Contains(id)).ToHashSet(StringComparer.Ordinal);
    }
}
