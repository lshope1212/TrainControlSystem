using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TrackModel.Wpf.ViewModels;
using TrainControl.Contracts.Enums;

namespace TrackModel.Wpf.Controls;

/// <summary>Scalable schematic renderer with click/keyboard block selection.
/// Coordinates belong to presentation; layout connectivity comes from domain data.</summary>
public sealed class TrackDiagram : FrameworkElement
{
    public static readonly DependencyProperty BlocksProperty = DependencyProperty.Register(nameof(Blocks), typeof(IEnumerable), typeof(TrackDiagram),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedBlockProperty = DependencyProperty.Register(nameof(SelectedBlock), typeof(BlockViewModel), typeof(TrackDiagram),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(nameof(Revision), typeof(int), typeof(TrackDiagram),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public IEnumerable? Blocks { get => (IEnumerable?)GetValue(BlocksProperty); set => SetValue(BlocksProperty, value); }
    public BlockViewModel? SelectedBlock { get => (BlockViewModel?)GetValue(SelectedBlockProperty); set => SetValue(SelectedBlockProperty, value); }
    public int Revision { get => (int)GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
    private readonly Dictionary<BlockViewModel, Rect> _hits = [];
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(20, 38, 62));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(96, 118, 147));
    private static readonly Pen TrackPen = new(Muted, 2);

    public TrackDiagram() { Focusable = true; Cursor = Cursors.Hand; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        var blocks = Blocks?.Cast<BlockViewModel>().ToList() ?? [];
        _hits.Clear();
        if (blocks.Count == 0) { Label(dc, "Import a layout to see the track.", new(40, 60), 20); return; }
        var points = Place(blocks);
        var edges = new HashSet<string>();
        foreach (var b in blocks)
        {
            foreach (var id in b.Connections)
            {
                var target = blocks.FirstOrDefault(x => x.Id == id);
                if (target is null) continue;
                var key = string.CompareOrdinal(b.Id, id) < 0 ? b.Id + ":" + id : id + ":" + b.Id;
                if (!edges.Add(key)) continue;
                var a = points[b]; var z = points[target];
                // Route the ends of the loop around the main and return rows.
                if (Math.Abs(a.Y - z.Y) > 150 && Math.Abs(a.X - z.X) < 10)
                {
                    var outside = a.X > 600 ? a.X + 70 : a.X - 70;
                    var geometry = new StreamGeometry();
                    using (var ctx = geometry.Open())
                    {
                        ctx.BeginFigure(a, false, false);
                        ctx.BezierTo(new(outside, a.Y), new(outside, z.Y), z, true, false);
                    }
                    dc.DrawGeometry(null, TrackPen, geometry);
                }
                else dc.DrawLine(TrackPen, a, z);
            }
        }
        foreach (var b in blocks)
        {
            var p = points[b];
            var r = new Rect(p.X - 31, p.Y - 14, 62, 28);
            _hits[b] = new Rect(p.X - 37, p.Y - 32, 74, 70);
            dc.DrawRoundedRectangle(b.StateBrush, new Pen(b == SelectedBlock ? Brushes.DodgerBlue : Brushes.White, b == SelectedBlock ? 3 : 1), r, 2, 2);
            if (b.HasFailure) Label(dc, "×", new(p.X - 8, p.Y - 17), 24, Brushes.White);
            Label(dc, b.Id, new(p.X - 15, p.Y + 20), 15, Muted);
            if (b.HasSwitch)
            {
                var diamond = new StreamGeometry();
                using (var ctx = diamond.Open())
                {
                    ctx.BeginFigure(new(p.X - 36, p.Y - 3), true, true);
                    ctx.PolyLineTo(new[] { new Point(p.X - 30, p.Y - 9), new Point(p.X - 24, p.Y - 3), new Point(p.X - 30, p.Y + 3) }, true, false);
                }
                dc.DrawGeometry(Brushes.DodgerBlue, null, diamond);
                Label(dc, b.Switch, new(p.X - 32, p.Y - 36), 11, Muted);
            }
            if (b.Station.Length > 0)
            {
                dc.DrawLine(new Pen(Brushes.SlateGray, 5), new(p.X - 35, p.Y - 52), new(p.X + 35, p.Y - 52));
                Label(dc, b.Station, new(p.X - 40, p.Y - 78), 15);
            }
            if (b.HasSignal)
            {
                var color = b.SignalState switch { SignalState.Green => Brushes.ForestGreen, SignalState.Red => Brushes.IndianRed,
                    SignalState.Yellow => Brushes.DarkOrange, _ => Brushes.SlateGray };
                dc.DrawEllipse(color, null, new(p.X + 36, p.Y - 22), 4, 7);
            }
            if (b.HasCrossing) Label(dc, b.Crossing, new(p.X - 32, p.Y - 106), 12, Muted);
            if (b.IsOccupied) Label(dc, "Train " + b.Train + " →", new(p.X - 34, p.Y + 45), 15);
            if (b.HasFailure) Label(dc, b.FailureSummary, new(p.X - 40, p.Y + 65), 12, Brushes.DarkOrange);
        }
        Label(dc, "Click a block to inspect it  •  Arrow keys change selection", new(35, 675), 14, Muted);
        if (blocks.Any(b => b.Section == "Bypass")) Label(dc, "Bypass", new(520, 135), 16, Muted);
        if (blocks.Any(b => b.Section == "Yard")) Label(dc, "Yard", new(40, 350), 16, Muted);
    }

    private static Dictionary<BlockViewModel, Point> Place(List<BlockViewModel> blocks)
    {
        var result = new Dictionary<BlockViewModel, Point>();
        var hasReturn = blocks.Any(b => b.Section == "Return");
        if (hasReturn)
        {
            foreach (var section in blocks.GroupBy(b => b.Section))
            {
                var list = section.OrderBy(b => b.Number).ToList();
                for (var i = 0; i < list.Count; i++)
                {
                    var x = section.Key switch { "Bypass" => 395 + i * 85, "Yard" => i == 2 ? 145 : 65,
                        "Return" => 930 - i * 85, _ => 165 + i * 85 };
                    var y = section.Key switch { "Bypass" => 190, "Yard" => i == 0 ? 405 : 465, "Return" => 550, _ => 320 };
                    result[list[i]] = new(x, y);
                }
            }
        }
        else
        {
            // General imported layouts: serpentine rows, with all actual graph edges drawn.
            const int columns = 8;
            for (var i = 0; i < blocks.Count; i++)
            {
                var row = i / columns;
                var col = row % 2 == 0 ? i % columns : columns - 1 - i % columns;
                result[blocks[i]] = new(110 + col * 125, 220 + row * 150);
            }
        }
        return result;
    }

    private void Label(DrawingContext dc, string text, Point p, double size, Brush? brush = null) =>
        dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush ?? Ink, VisualTreeHelper.GetDpi(this).PixelsPerDip), p);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var hit = _hits.FirstOrDefault(pair => pair.Value.Contains(e.GetPosition(this)));
        if (hit.Key is not null) SetCurrentValue(SelectedBlockProperty, hit.Key);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var blocks = Blocks?.Cast<BlockViewModel>().ToList() ?? [];
        if (blocks.Count == 0 || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) { base.OnKeyDown(e); return; }
        var index = blocks.IndexOf(SelectedBlock!);
        var delta = e.Key is Key.Left or Key.Up ? -1 : 1;
        SetCurrentValue(SelectedBlockProperty, blocks[(index + delta + blocks.Count) % blocks.Count]);
        e.Handled = true;
    }
}
