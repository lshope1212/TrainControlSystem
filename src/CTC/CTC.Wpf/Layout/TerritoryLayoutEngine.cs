using System.Windows;
using CTC.Wpf.ViewModels;

namespace CTC.Wpf.Layout;

/// <summary>
/// Computes the territory schematic for one line's blocks. Each block is placed along its
/// section's path from <see cref="TerritoryLayoutProvider"/>, in block-number order; sections
/// (or whole lines) without a path get a generic row layout. Connections are taken from the
/// blocks' ConnectedBlockIds, never inferred from block numbers. No WPF rendering here.
/// </summary>
public static class TerritoryLayoutEngine
{
    /// <summary>Gap left between consecutive blocks of a section, in layout units.</summary>
    private const double BlockGap = 4;

    /// <summary>Space around the track for labels, badges and station names.</summary>
    private const double Margin = 140;

    private const double FallbackBlockLength = 50;
    private const int FallbackBlocksPerRow = 20;
    private const double FallbackRowSpacing = 70;
    private const double FallbackSectionSpacing = 110;

    public static TerritoryLayout Compute(string? lineId, IEnumerable<BlockViewModel>? blocks)
    {
        var lineBlocks = blocks?.Where(block => block is not null).ToList() ?? [];
        if (lineBlocks.Count == 0)
        {
            return TerritoryLayout.Empty;
        }

        var schematic = TerritoryLayoutProvider.Find(lineId);
        var placements = new Dictionary<string, TerritoryBlockPlacement>();
        var unplacedSections = new List<IGrouping<string, BlockViewModel>>();

        var sections = lineBlocks
            .GroupBy(block => block.Section ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .OrderBy(section => section.Min(block => block.Number));

        foreach (var section in sections)
        {
            var path = schematic?.FindSection(section.Key);
            if (path is null || path.Points.Count < 2)
            {
                unplacedSections.Add(section);
                continue;
            }

            var points = path.Points.Select(point => new Point(point.X * schematic!.ScaleX, point.Y * schematic.ScaleY)).ToList();
            PlaceAlong(points, section.OrderBy(block => block.Number).ToList(), path.LabelSide, path.BadgeSide, placements);
        }

        // Unknown sections go in rows under whatever was placed from the schematic.
        double fallbackTop = placements.Count == 0 ? 0 : placements.Values.SelectMany(p => p.Points).Max(p => p.Y) + FallbackSectionSpacing;
        foreach (var section in unplacedSections)
        {
            var ordered = section.OrderBy(block => block.Number).ToList();
            var serpentine = FallbackPath(ordered.Count, fallbackTop);
            PlaceAlong(serpentine, ordered, -1, null, placements);
            fallbackTop = serpentine.Max(point => point.Y) + FallbackSectionSpacing;
        }

        var terminals = BuildTerminals(schematic, lineBlocks, placements);
        var connections = BuildConnections(lineBlocks, placements);

        return Normalize(placements, connections, terminals, usedFallback: unplacedSections.Count > 0);
    }

    /// <summary>Spreads blocks evenly along the path (a schematic, so lengths are not to scale).</summary>
    private static void PlaceAlong(List<Point> path, List<BlockViewModel> blocks, int labelSide, int? badgeSide, Dictionary<string, TerritoryBlockPlacement> placements)
    {
        double length = PolylineMath.Length(path);
        double slot = length / blocks.Count;
        double gap = Math.Min(BlockGap, slot * 0.2);

        for (int i = 0; i < blocks.Count; i++)
        {
            var points = PolylineMath.SubPath(path, i * slot + gap / 2, (i + 1) * slot - gap / 2);

            // Duplicate IDs within a line are a data error; the first block keeps the spot.
            placements.TryAdd(blocks[i].BlockId, new TerritoryBlockPlacement(blocks[i].BlockId, points, labelSide, badgeSide));
        }
    }

    /// <summary>A left-to-right, right-to-left serpentine holding <paramref name="blockCount"/> blocks.</summary>
    private static List<Point> FallbackPath(int blockCount, double top)
    {
        int rows = (blockCount + FallbackBlocksPerRow - 1) / FallbackBlocksPerRow;
        double rowLength = Math.Min(blockCount, FallbackBlocksPerRow) * FallbackBlockLength;
        var points = new List<Point>();

        for (int row = 0; row < rows; row++)
        {
            int blocksInRow = row == rows - 1 ? blockCount - row * FallbackBlocksPerRow : FallbackBlocksPerRow;
            double y = top + row * FallbackRowSpacing;
            bool leftToRight = row % 2 == 0;
            double start = leftToRight ? 0 : rowLength;
            double end = leftToRight ? blocksInRow * FallbackBlockLength : rowLength - blocksInRow * FallbackBlockLength;

            points.Add(new Point(start, y));
            points.Add(new Point(end, y));
        }

        return points;
    }

    private static List<TerritoryTerminal> BuildTerminals(TerritoryLineSchematic? schematic, List<BlockViewModel> blocks, Dictionary<string, TerritoryBlockPlacement> placements)
    {
        var terminals = new List<TerritoryTerminal>();
        foreach (var terminal in schematic?.Terminals ?? [])
        {
            var blockIds = terminal.BlockNumbers
                .Select(number => blocks.FirstOrDefault(block => block.Number == number)?.BlockId)
                .Where(id => id is not null && placements.ContainsKey(id))
                .Select(id => id!)
                .ToList();

            // A yard with nothing loaded next to it would only confuse the dispatcher.
            if (blockIds.Count > 0)
            {
                var position = new Point(terminal.Position.X * schematic!.ScaleX, terminal.Position.Y * schematic.ScaleY);
                terminals.Add(new TerritoryTerminal(terminal.Label, position, blockIds));
            }
        }

        return terminals;
    }

    /// <summary>Undirected, deduplicated connections between placed blocks of this line.</summary>
    private static List<TerritoryConnection> BuildConnections(List<BlockViewModel> blocks, Dictionary<string, TerritoryBlockPlacement> placements)
    {
        var seen = new HashSet<(string, string)>();
        var connections = new List<TerritoryConnection>();

        foreach (var block in blocks)
        {
            foreach (var otherId in block.ConnectedBlockIds)
            {
                if (otherId == block.BlockId || !placements.ContainsKey(block.BlockId) || !placements.ContainsKey(otherId))
                {
                    continue;
                }

                var key = string.CompareOrdinal(block.BlockId, otherId) < 0 ? (block.BlockId, otherId) : (otherId, block.BlockId);
                if (seen.Add(key))
                {
                    connections.Add(new TerritoryConnection(key.Item1, key.Item2));
                }
            }
        }

        return connections;
    }

    /// <summary>Shifts everything so the bounds (with margin) start at the origin.</summary>
    private static TerritoryLayout Normalize(
        Dictionary<string, TerritoryBlockPlacement> placements,
        List<TerritoryConnection> connections,
        List<TerritoryTerminal> terminals,
        bool usedFallback)
    {
        var allPoints = placements.Values.SelectMany(placement => placement.Points)
            .Concat(terminals.Select(terminal => terminal.Position))
            .ToList();

        double minX = allPoints.Min(point => point.X) - Margin;
        double minY = allPoints.Min(point => point.Y) - Margin;
        double maxX = allPoints.Max(point => point.X) + Margin;
        double maxY = allPoints.Max(point => point.Y) + Margin;
        var offset = new Vector(-minX, -minY);

        var shifted = placements.ToDictionary(
            pair => pair.Key,
            pair => new TerritoryBlockPlacement(pair.Key, pair.Value.Points.Select(point => point + offset).ToList(), pair.Value.LabelSide, pair.Value.BadgeSide));

        var shiftedTerminals = terminals
            .Select(terminal => terminal with { Position = terminal.Position + offset })
            .ToList();

        return new TerritoryLayout(shifted, connections, shiftedTerminals, new Rect(0, 0, maxX - minX, maxY - minY), usedFallback);
    }
}
