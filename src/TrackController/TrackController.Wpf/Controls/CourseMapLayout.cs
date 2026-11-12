using System.Text.Json;
using System.Windows;
using TrackController.Wpf.Layout;
using TrackController.Contracts.Messages;

namespace TrackController.Wpf.Controls;

internal sealed record CourseMapLayout(
    IReadOnlyDictionary<string, CourseMapLayout.Block> Blocks,
    IReadOnlyList<TerritoryTerminal> Terminals)
{
    internal sealed record Block(string Label, TerritoryBlockPlacement Placement, Point PresetMidpoint)
    {
        // Existing presets measure along the raw path before scaling its axes for display.
        public bool Matches(WaysideBlockStatus status) =>
            (PresetMidpoint - new Point(status.MapX, status.MapY)).Length <= 0.02 ||
            (Placement.Midpoint - new Point(status.MapX, status.MapY)).Length <= 0.02;
    }

    private sealed record BlockDefinition(string Id, string Line, string Section, int Number);
    private static readonly Lazy<IReadOnlyDictionary<string, CourseMapLayout>> Layouts = new(Load);

    public static CourseMapLayout? Find(string line) =>
        TerritoryLayoutProvider.Find(line) is { } schematic ? Layouts.Value.GetValueOrDefault(schematic.Key) : null;

    private static IReadOnlyDictionary<string, CourseMapLayout> Load()
    {
        using var source = typeof(CourseMapLayout).Assembly.GetManifestResourceStream("TrackController.CourseLines.json")!;
        using var document = JsonDocument.Parse(source);
        var blocks = document.RootElement.GetProperty("blocks").EnumerateArray().Select(block => new BlockDefinition(
            block.GetProperty("id").GetString()!,
            block.GetProperty("lineId").GetString()!,
            block.GetProperty("section").GetString()!,
            block.GetProperty("number").GetInt32()));
        return blocks.GroupBy(block => block.Line).ToDictionary(
            line => line.Key.ToUpperInvariant(),
            line => Build(TerritoryLayoutProvider.Find(line.Key)!, line));
    }

    private static CourseMapLayout Build(TerritoryLineSchematic schematic, IEnumerable<BlockDefinition> line)
    {
        Point Scale(Point point) => new(point.X * schematic.ScaleX, point.Y * schematic.ScaleY);

        var blocks = new Dictionary<string, Block>(StringComparer.Ordinal);
        var definitions = line.ToArray();
        foreach (var section in definitions.GroupBy(block => block.Section))
        {
            var path = schematic.FindSection(section.Key);
            if (path is null) continue;
            var points = path.Points.Select(Scale).ToArray();
            var ordered = section.OrderBy(block => block.Number).ToArray();
            double slot = PolylineMath.Length(points) / ordered.Length;
            double presetSlot = PolylineMath.Length(path.Points) / ordered.Length;
            double halfGap = Math.Min(4, slot * 0.2) / 2;
            for (int i = 0; i < ordered.Length; i++)
            {
                var block = ordered[i];
                var segment = PolylineMath.SubPath(points, i * slot + halfGap, (i + 1) * slot - halfGap);
                var midpoint = Scale(PolylineMath.PointAndDirectionAt(path.Points, (i + 0.5) * presetSlot).Point);
                blocks.Add(block.Id, new Block($"{block.Section}{block.Number}",
                    new TerritoryBlockPlacement(block.Id, segment, path.LabelSide, path.BadgeSide), midpoint));
            }
        }

        var terminals = schematic.Terminals.Select(terminal => new TerritoryTerminal(terminal.Label, Scale(terminal.Position),
            definitions.Where(block => terminal.BlockNumbers.Contains(block.Number)).Select(block => block.Id).ToArray()));
        return new CourseMapLayout(blocks, terminals.ToArray());
    }
}
