using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TrackController.Contracts.Messages;
using TrainControl.Contracts.Enums;

namespace TrackController.Wpf.Controls;

/// <summary>Territory-only schematic; loaded coordinates never change physical connectivity.</summary>
public sealed class TerritoryMap : FrameworkElement
{
    public static readonly DependencyProperty BlocksProperty = DependencyProperty.Register(nameof(Blocks), typeof(IEnumerable), typeof(TerritoryMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, BlocksChanged));
    public static readonly DependencyProperty NetworkBlocksProperty = DependencyProperty.Register(nameof(NetworkBlocks), typeof(IEnumerable), typeof(TerritoryMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedBlockIdProperty = DependencyProperty.Register(nameof(SelectedBlockId), typeof(string), typeof(TerritoryMap),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public IEnumerable? Blocks { get => (IEnumerable?)GetValue(BlocksProperty); set => SetValue(BlocksProperty, value); }
    public IEnumerable? NetworkBlocks { get => (IEnumerable?)GetValue(NetworkBlocksProperty); set => SetValue(NetworkBlocksProperty, value); }
    public string SelectedBlockId { get => (string)GetValue(SelectedBlockIdProperty); set => SetValue(SelectedBlockIdProperty, value); }
    public static RoutedUICommand FitCommand { get; } = new("Fit territory", "Fit", typeof(TerritoryMap));
    public static RoutedUICommand ZoomInCommand { get; } = new("Zoom in", "ZoomIn", typeof(TerritoryMap));
    public static RoutedUICommand ZoomOutCommand { get; } = new("Zoom out", "ZoomOut", typeof(TerritoryMap));
    private readonly Dictionary<string, Point> _targets = [];
    private string _territoryKey = "";
    private double _zoom = 1;
    private Vector _pan;
    private Point _mouseDown, _previousMouse;
    private bool _dragged;
    private WaysideBlockStatus[] Visible => Blocks?.Cast<WaysideBlockStatus>().ToArray() ?? [];
    private static Brush Ink(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;

    public TerritoryMap()
    {
        ClipToBounds = true;
        Focusable = true;
        CommandBindings.Add(new(FitCommand, (_, _) => Fit()));
        CommandBindings.Add(new(ZoomInCommand, (_, _) => Zoom(1.3, new(ActualWidth / 2, ActualHeight / 2))));
        CommandBindings.Add(new(ZoomOutCommand, (_, _) => Zoom(1 / 1.3, new(ActualWidth / 2, ActualHeight / 2))));
    }

    private static void BlocksChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var map = (TerritoryMap)sender;
        string key = string.Join("|", map.Visible.Select(block => block.BlockId).Order(StringComparer.Ordinal));
        if (key != map._territoryKey) { map._territoryKey = key; map.Fit(); }
    }
    private void Fit() { _zoom = 1; _pan = default; InvalidateVisual(); }
    private void Zoom(double factor, Point anchor)
    {
        double next = Math.Clamp(_zoom * factor, 0.6, 12);
        var offset = anchor - new Point(ActualWidth / 2, ActualHeight / 2);
        _pan = offset - (offset - _pan) * (next / _zoom);
        _zoom = next;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        _targets.Clear();
        var blocks = Visible;
        if (blocks.Length == 0 || ActualWidth < 1 || ActualHeight < 1) return;
        var owned = blocks.ToDictionary(block => block.BlockId);
        var network = (NetworkBlocks?.Cast<WaysideBlockStatus>() ?? blocks).ToDictionary(block => block.BlockId);
        double minX = blocks.Min(block => block.MapX), maxX = blocks.Max(block => block.MapX);
        double minY = blocks.Min(block => block.MapY), maxY = blocks.Max(block => block.MapY);
        // A schematic has no distance scale: expand each axis to use the available viewport.
        // This keeps parallel corridors readable even in the short laptop-height map panel.
        double scaleX = Math.Max(1, ActualWidth - 120) / Math.Max(1, maxX - minX) * _zoom;
        double scaleY = Math.Max(1, ActualHeight - 80) / Math.Max(1, maxY - minY) * _zoom;
        Point At(WaysideBlockStatus block) => new(ActualWidth / 2 + (block.MapX - (minX + maxX) / 2) * scaleX + _pan.X,
            ActualHeight / 2 + (block.MapY - (minY + maxY) / 2) * scaleY + _pan.Y);
        foreach (var block in blocks) _targets[block.BlockId] = At(block);
        var drawnEdges = new HashSet<(string, string)>();
        var ports = new List<(Point Point, string Label)>();
        foreach (var source in network.Values)
        foreach (string next in source.NextBlockIds)
        {
            if (!network.TryGetValue(next, out var destination) || (!owned.ContainsKey(source.BlockId) && !owned.ContainsKey(next))) continue;
            var key = string.CompareOrdinal(source.BlockId, next) < 0 ? (source.BlockId, next) : (next, source.BlockId);
            if (!drawnEdges.Add(key)) continue;
            bool bidirectional = destination.NextBlockIds.Contains(source.BlockId);
            var a = At(source); var b = At(destination);
            if (owned.ContainsKey(source.BlockId) && owned.ContainsKey(next))
            {
                dc.DrawLine(new Pen(Ink("#B5C2CF"), 4), a, b);
                if (!bidirectional && (b - a).Length > 24) Arrow(a + (b - a) * 0.58, b - a);
            }
            else
            {
                bool outgoing = owned.ContainsKey(source.BlockId);
                Point home = outgoing ? a : b;
                Vector direction = outgoing ? b - a : a - b;
                if (direction.Length < 0.001) direction = new(1, 0);
                direction.Normalize();
                Point port = home + direction * 34;
                var pen = new Pen(Ink("#7C3AED"), 2) { DashStyle = DashStyles.Dash };
                dc.DrawLine(pen, home, port);
                Arrow(outgoing || bidirectional ? port : home + direction * 12, outgoing || bidirectional ? direction : -direction, "#7C3AED");
                ports.Add((port + direction * 12, ShortId(outgoing ? next : source.BlockId)));
            }
        }

        var labels = _targets.Values.Select(point => new Rect(point.X - 8, point.Y - 8, 16, 16)).ToList();
        foreach (var block in blocks)
        {
            Point point = At(block);
            Brush fill = !block.IsFresh || block.Occupancy == OccupancyState.Unknown ? Ink("#94A3B8")
                : block.IsFaulted || block.Occupancy == OccupancyState.Occupied ? Ink("#DC4740") : Ink("#348463");
            if (block.Maintenance != "Open") dc.DrawEllipse(null, new Pen(Ink("#BA7513"), 2), point, 10, 10);
            if (block.BlockId == SelectedBlockId) dc.DrawEllipse(null, new Pen(Ink("#2563EB"), 3), point, 13, 13);
            if (block.HasSwitch)
            {
                var shape = new StreamGeometry();
                using (var context = shape.Open())
                {
                    context.BeginFigure(point + new Vector(0, -8), true, true);
                    context.PolyLineTo([point + new Vector(8, 0), point + new Vector(0, 8), point + new Vector(-8, 0)], true, false);
                }
                dc.DrawGeometry(fill, new Pen(Brushes.White, 1), shape);
            }
            else dc.DrawEllipse(fill, new Pen(Brushes.White, 1), point, 5, 5);
            if (block.HasCrossing) dc.DrawLine(new Pen(Ink("#344256"), 2), point + new Vector(-7, -7), point + new Vector(7, 7));
            if (!string.IsNullOrWhiteSpace(block.StationName)) dc.DrawEllipse(null, new Pen(Ink("#6D28D9"), 1.5), point, 8, 8);
        }
        foreach (var block in blocks.OrderByDescending(block => block.BlockId == SelectedBlockId).ThenByDescending(block => block.HasSwitch))
            Label(ShortId(block.BlockId), At(block), Ink("#203047"), block.BlockId == SelectedBlockId);
        foreach (var port in ports) Label(port.Label, port.Point, Ink("#7C3AED"), false);

        void Arrow(Point tip, Vector direction, string color = "#718397")
        {
            if (direction.Length < 0.001) return;
            direction.Normalize(); var perpendicular = new Vector(-direction.Y, direction.X);
            dc.DrawLine(new Pen(Ink(color), 1.5), tip, tip - direction * 5 + perpendicular * 3);
            dc.DrawLine(new Pen(Ink(color), 1.5), tip, tip - direction * 5 - perpendicular * 3);
        }
        void Label(string text, Point point, Brush color, bool force)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, color, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            foreach (var offset in new[] { new Vector(-formatted.Width / 2, -24), new Vector(12, -7), new Vector(-formatted.Width - 12, -7), new Vector(-formatted.Width / 2, 12) })
            {
                var rect = new Rect(point + offset, new Size(formatted.Width + 3, 16));
                if (!force && labels.Any(existing => existing.IntersectsWith(rect))) continue;
                dc.DrawRoundedRectangle(Brushes.White, null, rect, 2, 2);
                dc.DrawText(formatted, rect.TopLeft);
                labels.Add(rect); return;
            }
        }
    }

    private static string ShortId(string id) => id.Split('-')[^1];
    protected override void OnMouseWheel(MouseWheelEventArgs e) { Zoom(e.Delta > 0 ? 1.2 : 1 / 1.2, e.GetPosition(this)); e.Handled = true; }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus(); _mouseDown = _previousMouse = e.GetPosition(this); _dragged = false; CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        Point point = e.GetPosition(this);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
        {
            if ((point - _mouseDown).Length > 4) _dragged = true;
            if (_dragged) { _pan += point - _previousMouse; InvalidateVisual(); }
            _previousMouse = point;
        }
        else
        {
            var hit = _targets.FirstOrDefault(item => (item.Value - point).Length <= 12);
            var block = Visible.FirstOrDefault(item => item.BlockId == hit.Key);
            ToolTip = block is null ? null : $"{block.BlockId}{(string.IsNullOrEmpty(block.StationName) ? "" : " · " + block.StationName)}\n{block.Occupancy} · {block.Maintenance}"
                + (block.HasSwitch ? $"\nSwitch: {block.ReportedSwitch}" : "") + (block.HasSignal ? $"\nSignal: {block.ReportedSignal}" : "");
            Cursor = block is null ? Cursors.SizeAll : Cursors.Hand;
        }
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        if (!_dragged)
        {
            var hit = _targets.OrderBy(item => (item.Value - e.GetPosition(this)).Length).FirstOrDefault();
            if (hit.Key is not null && (hit.Value - e.GetPosition(this)).Length <= 14) SetCurrentValue(SelectedBlockIdProperty, hit.Key);
        }
        e.Handled = true;
    }
}
