using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TrackController.Wpf.Layout;
using TrackController.Contracts.Messages;
using TrainControl.Contracts.Enums;

namespace TrackController.Wpf.Controls;

public sealed partial class TerritoryMap
{
    public static readonly DependencyProperty LineNameProperty = DependencyProperty.Register(
        nameof(LineName), typeof(string), typeof(TerritoryMap),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public string LineName { get => (string)GetValue(LineNameProperty); set => SetValue(LineNameProperty, value); }

    private static readonly Brush HandoffInk = Frozen(Ink("#6D28D9"));
    private static readonly Brush TrackInk = Frozen(Ink("#26374B"));
    private static readonly Brush ClearInk = Frozen(Ink("#368653"));
    private static readonly Brush OccupiedInk = Frozen(Ink("#C84036"));
    private static readonly Brush UnknownInk = Frozen(Ink("#9BA8B8"));
    private static readonly Brush SelectedInk = Frozen(Ink("#2563EB"));
    private static readonly Brush MaintenanceInk = Frozen(Ink("#BB721F"));
    private static readonly Pen ConnectionPen = Frozen(new Pen(Ink("#ACB8C5"), 2.5));
    private static readonly Pen HandoffPen = Frozen(new Pen(HandoffInk, 2) { DashStyle = DashStyles.Dash });
    private static readonly Pen YardPen = Frozen(new Pen(Ink("#8292A5"), 2) { DashStyle = DashStyles.Dash });
    private static readonly Pen StationPen = Frozen(new Pen(Ink("#344256"), 2.5));
    private static readonly Pen CrossingPen = Frozen(new Pen(HandoffInk, 2));

    private readonly Dictionary<string, TerritoryBlockPlacement> _courseTargets = [];

    private bool DrawCourseMap(DrawingContext dc, WaysideBlockStatus[] blocks, IReadOnlyDictionary<string, WaysideBlockStatus> network)
    {
        var layout = CourseMapLayout.Find(LineName);
        if (layout is null || blocks.Any(block => !layout.Blocks.TryGetValue(block.BlockId, out var entry) || !entry.Matches(block)))
            return false;

        var owned = blocks.Select(block => block.BlockId).ToHashSet(StringComparer.Ordinal);
        var terminals = layout.Terminals.Where(terminal => terminal.BlockIds.Any(owned.Contains)).ToArray();
        var points = blocks.SelectMany(block => layout.Blocks[block.BlockId].Placement.Points)
            .Concat(terminals.Select(terminal => terminal.Position)).ToArray();
        var bounds = new Rect(new Point(points.Min(p => p.X), points.Min(p => p.Y)),
            new Point(points.Max(p => p.X), points.Max(p => p.Y)));
        var center = bounds.Location + new Vector(bounds.Width / 2, bounds.Height / 2);
        var viewCenter = new Point(ActualWidth / 2, ActualHeight / 2) + _pan;
        double scale = Math.Min(Math.Max(1, ActualWidth - 124) / Math.Max(1, bounds.Width),
            Math.Max(1, ActualHeight - 90) / Math.Max(1, bounds.Height)) * _zoom;
        Point Project(Point point) => viewCenter + (point - center) * scale;

        foreach (var block in blocks)
        {
            var placement = layout.Blocks[block.BlockId].Placement;
            _courseTargets.Add(block.BlockId, new TerritoryBlockPlacement(block.BlockId,
                placement.Points.Select(Project).ToArray(), placement.LabelSide, placement.BadgeSide));
        }

        var labels = new List<Rect>();
        var ports = new List<(Point Position, Vector Direction, string Label)>();
        var edges = new HashSet<TerritoryConnection>();
        foreach (var source in network.Values)
        foreach (string destinationId in source.NextBlockIds)
        {
            bool fromOwned = owned.Contains(source.BlockId);
            bool toOwned = owned.Contains(destinationId);
            if ((!fromOwned && !toOwned) || !network.TryGetValue(destinationId, out var destination) ||
                !layout.Blocks.TryGetValue(source.BlockId, out var from) || !layout.Blocks.TryGetValue(destinationId, out var to))
                continue;

            var edge = string.CompareOrdinal(source.BlockId, destinationId) < 0
                ? new TerritoryConnection(source.BlockId, destinationId) : new TerritoryConnection(destinationId, source.BlockId);
            if (!edges.Add(edge)) continue;
            var (start, end) = ClosestEndpoints(from.Placement, to.Placement);
            if (fromOwned && toOwned)
            {
                dc.DrawLine(ConnectionPen, Project(start), Project(end));
                continue;
            }

            var home = Project(fromOwned ? start : end);
            var other = Project(fromOwned ? end : start);
            Vector direction = other - home;
            if (direction.Length < 1)
                direction = Project((fromOwned ? to : from).Placement.Midpoint) - home;
            if (direction.Length < 0.001) continue;
            direction.Normalize();
            var port = home + direction * 19;
            dc.DrawLine(HandoffPen, home, port);
            bool outgoing = fromOwned || destination.NextBlockIds.Contains(source.BlockId);
            Vector arrow = outgoing ? direction : -direction;
            Vector perpendicular = new(-arrow.Y, arrow.X);
            Point tip = outgoing ? port : home + direction * 6;
            dc.DrawLine(CrossingPen, tip, tip - arrow * 4 + perpendicular * 3);
            dc.DrawLine(CrossingPen, tip, tip - arrow * 4 - perpendicular * 3);
            ports.Add((port, direction, (fromOwned ? to : from).Label));
        }

        foreach (var terminal in terminals)
        {
            Point position = Project(terminal.Position);
            foreach (string id in terminal.BlockIds.Where(owned.Contains))
            {
                var block = _courseTargets[id];
                Point endpoint = (block.Start - position).LengthSquared < (block.End - position).LengthSquared ? block.Start : block.End;
                dc.DrawLine(YardPen, position, endpoint);
            }
            dc.DrawEllipse(Brushes.White, StationPen, position, 4, 4);
            Label(terminal.Label, position, new Vector(0, -1), 16, TrackInk, preferred: true);
        }

        foreach (var block in blocks)
        {
            var placement = _courseTargets[block.BlockId];
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(placement.Start, false, false);
                context.PolyLineTo(placement.Points.Skip(1).ToArray(), true, false);
            }
            geometry.Freeze();
            if (block.BlockId == SelectedBlockId)
            {
                Stroke(SelectedInk, 12, PenLineCap.Round);
                Stroke(Brushes.White, 9, PenLineCap.Round);
            }
            if (block.Maintenance != "Open") Stroke(MaintenanceInk, 8);
            Stroke(!block.IsFresh || block.Occupancy == OccupancyState.Unknown ? UnknownInk
                : block.IsFaulted || block.Occupancy == OccupancyState.Occupied ? OccupiedInk : ClearInk, 4.5);

            Point point = placement.Midpoint;
            if (!string.IsNullOrWhiteSpace(block.StationName))
            {
                Vector tangent = placement.Direction * 7;
                Vector offset = placement.BadgeNormal * 7;
                dc.DrawLine(StationPen, point + offset - tangent, point + offset + tangent);
            }
            if (block.HasCrossing)
            {
                Vector diagonal = (placement.Direction + placement.BadgeNormal) * 4;
                Vector other = (placement.Direction - placement.BadgeNormal) * 4;
                dc.DrawLine(CrossingPen, point - diagonal, point + diagonal);
                dc.DrawLine(CrossingPen, point - other, point + other);
            }
            if (block.HasSignal)
            {
                Brush signal = block.ReportedSignal switch
                {
                    SignalState.Green => ClearInk,
                    SignalState.Red => OccupiedInk,
                    _ => UnknownInk
                };
                dc.DrawEllipse(signal, new Pen(Brushes.White, 1), placement.Start + placement.BadgeNormal * 6, 3, 3);
            }

            void Stroke(Brush brush, double width, PenLineCap cap = PenLineCap.Flat) =>
                dc.DrawGeometry(null, new Pen(brush, width) { LineJoin = PenLineJoin.Round, StartLineCap = cap, EndLineCap = cap }, geometry);
        }

        foreach (var block in blocks.OrderByDescending(block => block.BlockId == SelectedBlockId))
        {
            var placement = _courseTargets[block.BlockId];
            if (!string.IsNullOrWhiteSpace(block.StationName))
                Label(block.StationName, placement.Midpoint, placement.BadgeNormal, 19, TrackInk, preferred: true);
            if (block.HasSwitch)
            {
                string position = block.ReportedSwitch switch
                {
                    SwitchPosition.Normal => "N",
                    SwitchPosition.Reverse => "R",
                    _ => "?"
                };
                Label($"SW {position}", placement.Midpoint, placement.BadgeNormal, 18, HandoffInk);
            }
        }
        foreach (var port in ports) Label(port.Label, port.Position, port.Direction, 12, HandoffInk);
        foreach (var block in blocks.OrderByDescending(block => block.BlockId == SelectedBlockId))
        {
            var placement = _courseTargets[block.BlockId];
            Label(layout.Blocks[block.BlockId].Label, placement.Midpoint, placement.LabelNormal, 13, TrackInk);
        }
        return true;

        void Label(string text, Point anchor, Vector normal, double offset, Brush brush, bool preferred = false)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), preferred ? 11 : 10, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var half = new Vector(formatted.Width / 2 + 2, formatted.Height / 2);
            Vector tangent = new(-normal.Y, normal.X);
            double distance = Math.Max(offset, Math.Abs(normal.X) * half.X + Math.Abs(normal.Y) * half.Y + 7);
            foreach (var shift in new[] { default(Vector), tangent * 12, -tangent * 12, normal * 15 })
            {
                Point labelCenter = anchor + normal * distance + shift;
                var rectangle = new Rect(labelCenter - half, labelCenter + half);
                if (labels.Any(label => label.IntersectsWith(rectangle)) ||
                    _courseTargets.Values.Any(placement => placement.DistanceTo(labelCenter) < half.Y + 4))
                    continue;
                dc.DrawRoundedRectangle(Brushes.White, null, rectangle, 2, 2);
                dc.DrawText(formatted, rectangle.TopLeft + new Vector(2, 0));
                labels.Add(rectangle);
                return;
            }
        }
    }

    private static (Point From, Point To) ClosestEndpoints(TerritoryBlockPlacement first, TerritoryBlockPlacement second) =>
        new (Point From, Point To)[]
        {
            (first.Start, second.Start), (first.Start, second.End),
            (first.End, second.Start), (first.End, second.End)
        }.MinBy(edge => (edge.From - edge.To).LengthSquared);

    private static T Frozen<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }

    private string? HitBlock(Point point, double radius)
    {
        string? nearest = null;
        foreach (var (id, placement) in _courseTargets)
        {
            double distance = placement.DistanceTo(point);
            if (distance > radius) continue;
            nearest = id;
            radius = distance;
        }
        foreach (var (id, center) in _targets)
        {
            double distance = (center - point).Length;
            if (distance > radius) continue;
            nearest = id;
            radius = distance;
        }
        return nearest;
    }
}
