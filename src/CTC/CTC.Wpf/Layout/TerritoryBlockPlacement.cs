using System.Windows;

namespace CTC.Wpf.Layout;

/// <summary>
/// Where one block is drawn on the territory schematic: a short polyline (in layout units)
/// along its section's path. Presentation only; never part of CTC state or the contracts.
/// </summary>
public sealed class TerritoryBlockPlacement
{
    public TerritoryBlockPlacement(string blockId, IReadOnlyList<Point> points, int labelSide, int? badgeSide = null)
    {
        if (points.Count < 2)
        {
            throw new ArgumentException("A block placement needs at least two points.", nameof(points));
        }

        BlockId = blockId;
        Points = points;
        LabelSide = labelSide >= 0 ? 1 : -1;
        BadgeSide = badgeSide is null ? -LabelSide : badgeSide >= 0 ? 1 : -1;

        (Midpoint, Direction) = PolylineMath.PointAndDirectionAt(points, PolylineMath.Length(points) / 2);
    }

    public string BlockId { get; }

    public IReadOnlyList<Point> Points { get; }

    public Point Start => Points[0];

    public Point End => Points[^1];

    /// <summary>Halfway along the block.</summary>
    public Point Midpoint { get; }

    /// <summary>Unit direction of travel (increasing block number) at the midpoint.</summary>
    public Vector Direction { get; }

    /// <summary>+1: the block label goes on the left of the travel direction; -1: on the right.</summary>
    public int LabelSide { get; }

    /// <summary>Side for station names and status badges, in the same terms as <see cref="LabelSide"/>.</summary>
    public int BadgeSide { get; }

    /// <summary>Unit normal pointing to the side the block label is drawn on.</summary>
    public Vector LabelNormal => LeftNormal * LabelSide;

    /// <summary>Unit normal pointing to the side station names and status badges are drawn on.</summary>
    public Vector BadgeNormal => LeftNormal * BadgeSide;

    private Vector LeftNormal => new Vector(Direction.Y, -Direction.X);

    /// <summary>Shortest distance from <paramref name="point"/> to the block's polyline.</summary>
    public double DistanceTo(Point point) => PolylineMath.DistanceTo(Points, point);
}

/// <summary>A visual-only terminal (e.g. the yard) and the blocks it is drawn joined to.</summary>
public sealed record TerritoryTerminal(string Label, Point Position, IReadOnlyList<string> BlockIds);

/// <summary>An undirected block-to-block connection, drawn once.</summary>
public readonly record struct TerritoryConnection(string FromBlockId, string ToBlockId);

/// <summary>Complete schematic geometry for one line's blocks.</summary>
public sealed class TerritoryLayout
{
    public static TerritoryLayout Empty { get; } = new TerritoryLayout(
        new Dictionary<string, TerritoryBlockPlacement>(), [], [], new Rect(0, 0, 1, 1), usedFallback: false);

    public TerritoryLayout(
        IReadOnlyDictionary<string, TerritoryBlockPlacement> placements,
        IReadOnlyList<TerritoryConnection> connections,
        IReadOnlyList<TerritoryTerminal> terminals,
        Rect bounds,
        bool usedFallback)
    {
        Placements = placements;
        Connections = connections;
        Terminals = terminals;
        Bounds = bounds;
        UsedFallback = usedFallback;
    }

    /// <summary>Placement of every block, by BlockId.</summary>
    public IReadOnlyDictionary<string, TerritoryBlockPlacement> Placements { get; }

    /// <summary>Deduplicated connections taken from the blocks' ConnectedBlockIds.</summary>
    public IReadOnlyList<TerritoryConnection> Connections { get; }

    public IReadOnlyList<TerritoryTerminal> Terminals { get; }

    /// <summary>Extent of the schematic including a margin for labels; always starts at (0, 0).</summary>
    public Rect Bounds { get; }

    /// <summary>True when any block was placed by the generic fallback (unknown line or section).</summary>
    public bool UsedFallback { get; }
}

internal static class PolylineMath
{
    public static double Length(IReadOnlyList<Point> points)
    {
        double length = 0;
        for (int i = 1; i < points.Count; i++)
        {
            length += (points[i] - points[i - 1]).Length;
        }

        return length;
    }

    public static (Point Point, Vector Direction) PointAndDirectionAt(IReadOnlyList<Point> points, double distance)
    {
        Vector lastDirection = new Vector(1, 0);
        for (int i = 1; i < points.Count; i++)
        {
            Vector segment = points[i] - points[i - 1];
            double length = segment.Length;
            if (length <= 0)
            {
                continue;
            }

            lastDirection = segment / length;
            if (distance <= length || i == points.Count - 1)
            {
                return (points[i - 1] + lastDirection * Math.Clamp(distance, 0, length), lastDirection);
            }

            distance -= length;
        }

        return (points[^1], lastDirection);
    }

    /// <summary>The part of the polyline between two distances along it.</summary>
    public static List<Point> SubPath(IReadOnlyList<Point> points, double from, double to)
    {
        var result = new List<Point> { PointAndDirectionAt(points, from).Point };
        double travelled = 0;
        for (int i = 1; i < points.Count; i++)
        {
            travelled += (points[i] - points[i - 1]).Length;
            if (travelled > from && travelled < to)
            {
                result.Add(points[i]);
            }
        }

        result.Add(PointAndDirectionAt(points, to).Point);
        return result;
    }

    public static double DistanceTo(IReadOnlyList<Point> points, Point point)
    {
        double best = double.MaxValue;
        for (int i = 1; i < points.Count; i++)
        {
            Vector segment = points[i] - points[i - 1];
            double lengthSquared = segment.LengthSquared;
            double t = lengthSquared <= 0 ? 0 : Math.Clamp(Vector.Multiply(point - points[i - 1], segment) / lengthSquared, 0, 1);
            best = Math.Min(best, (point - (points[i - 1] + segment * t)).Length);
        }

        return best;
    }
}
