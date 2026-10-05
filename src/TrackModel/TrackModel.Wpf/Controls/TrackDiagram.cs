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
    private static readonly Brush Ink = Brushes.Black;
    private static readonly Brush Muted = Brushes.Black;
    private static readonly Pen TrackPen = new(new SolidColorBrush(Color.FromRgb(96, 118, 147)), 2);

    public TrackDiagram() { Focusable = true; Cursor = Cursors.Hand; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        var blocks = Blocks?.Cast<BlockViewModel>().ToList() ?? [];
        _hits.Clear();
        if (blocks.Count == 0) { Label(dc, "No track loaded", new(40, 60), 22); return; }
        var points = Place(blocks);
        var hasReturn = blocks.Any(b => b.Section == "Return");
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
                DrawConnection(dc, b, target, a, z, hasReturn);
            }
        }
        foreach (var b in blocks)
        {
            var p = points[b];
            var r = new Rect(p.X - 31, p.Y - 14, 62, 28);
            _hits[b] = new Rect(p.X - 37, p.Y - 32, 74, 70);
            dc.DrawRoundedRectangle(b.StateBrush, new Pen(b == SelectedBlock ? Brushes.DodgerBlue : Brushes.White, b == SelectedBlock ? 3 : 1), r, 2, 2);
            if (b.HasFailure) Label(dc, "×", new(p.X - 8, p.Y - 17), 24, Brushes.White);
            Label(dc, b.Id, new(p.X - 15, p.Y + 20), 18, Muted);
            if (b.HasSwitch)
            {
                var diamond = new StreamGeometry();
                using (var ctx = diamond.Open())
                {
                    ctx.BeginFigure(new(p.X - 36, p.Y - 3), true, true);
                    ctx.PolyLineTo(new[] { new Point(p.X - 30, p.Y - 9), new Point(p.X - 24, p.Y - 3), new Point(p.X - 30, p.Y + 3) }, true, false);
                }
                dc.DrawGeometry(Brushes.DodgerBlue, null, diamond);
                Label(dc, b.Switch, new(p.X - 32, p.Y + (b.IsOccupied || b.HasFailure ? 90 : 45)), 14, Muted);
            }
            if (b.Station.Length > 0)
            {
                dc.DrawLine(new Pen(Brushes.SlateGray, 5), new(p.X - 35, p.Y - 52), new(p.X + 35, p.Y - 52));
                Label(dc, b.Station, new(p.X - 40, p.Y - 78), 18);
            }
            if (b.HasSignal)
            {
                var color = b.SignalState switch { SignalState.Green => Brushes.ForestGreen, SignalState.Red => Brushes.IndianRed,
                    SignalState.Yellow => Brushes.DarkOrange, _ => Brushes.SlateGray };
                dc.DrawEllipse(color, null, new(p.X + 36, p.Y - 22), 4, 7);
            }
            if (b.HasCrossing) Label(dc, b.Crossing, new(p.X - 32, p.Y + (b.IsOccupied || b.HasFailure ? 90 : 45)), 15, Muted);
            if (b.IsOccupied) Label(dc, "Train " + b.Train + " →", new(p.X - 34, p.Y + 45), 18,
                maxWidth: b.Section == "Yard" ? 120 : null);
            if (b.HasFailure) Label(dc, b.FailureSummary, new(p.X - 40, p.Y + 65), 15, Brushes.DarkOrange,
                maxWidth: b.Section == "Yard" ? 130 : null);
        }
        if (blocks.Any(b => b.Section == "Bypass")) Label(dc, "Bypass", new(600, 135), 19, Muted);
        if (blocks.Any(b => b.Section == "Yard")) Label(dc, "Yard", new(70, 75), 19, Muted);
    }

    private static void DrawConnection(DrawingContext dc, BlockViewModel source, BlockViewModel target,
        Point a, Point z, bool hasReturn)
    {
        if (!hasReturn) { dc.DrawLine(TrackPen, a, z); return; }
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            if ((source.Section == "Main" && target.Section == "Return" ||
                 source.Section == "Return" && target.Section == "Main") && Math.Abs(a.X - z.X) < 10)
            {
                // Equal offsets from the block side ports give mirrored loop ends.
                var direction = a.X > 600 ? 1 : -1;
                a.X += direction * 31; z.X += direction * 31;
                var outside = a.X + direction * 90;
                ctx.BeginFigure(a, false, false);
                ctx.BezierTo(new(outside, a.Y), new(outside, z.Y), z, true, false);
            }
            else if (source.Section == "Main" && target.Section == "Bypass" ||
                     source.Section == "Bypass" && target.Section == "Main")
            {
                var main = source.Section == "Main" ? a : z;
                var bypass = source.Section == "Bypass" ? a : z;
                var direction = bypass.X > main.X ? 1 : -1;
                const double radius = 40, control = radius * 0.5522847498;
                ctx.BeginFigure(new(main.X, main.Y - 14), false, false);
                ctx.LineTo(new(main.X, bypass.Y + radius), true, false);
                ctx.BezierTo(new(main.X, bypass.Y + radius - control),
                    new(main.X + direction * (radius - control), bypass.Y),
                    new(main.X + direction * radius, bypass.Y), true, false);
                ctx.LineTo(new(bypass.X - direction * 31, bypass.Y), true, false);
            }
            else if (source.Section == "Main" && target.Section == "Yard" ||
                     source.Section == "Yard" && target.Section == "Main")
            {
                var main = source.Section == "Main" ? a : z;
                var yard = source.Section == "Yard" ? a : z;
                main.X -= 31; yard.X -= 31;
                var outside = Math.Min(main.X, yard.X) - 30;
                ctx.BeginFigure(main, false, false);
                ctx.BezierTo(new(outside, main.Y), new(outside, yard.Y), yard, true, false);
            }
            else if (source.Section == "Yard" && target.Section == "Yard")
            {
                if (a.X > z.X) (a, z) = (z, a);
                a.X += 31; z.X -= 31;
                var middle = (a.X + z.X) / 2;
                ctx.BeginFigure(a, false, false);
                ctx.BezierTo(new(middle, a.Y), new(middle, z.Y), z, true, false);
            }
            else
            {
                ctx.BeginFigure(a, false, false);
                ctx.LineTo(z, true, false);
            }
        }
        dc.DrawGeometry(null, TrackPen, geometry);
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
                    var x = section.Key switch { "Bypass" => 420 + i * (list.Count > 1 ? 425d / (list.Count - 1) : 0), "Yard" => i == 0 ? 105 : 245,
                        "Return" => 930 - i * 85, _ => 165 + i * 85 };
                    var y = section.Key switch { "Bypass" => 190, "Yard" => i == 0 ? 150 : 100 + (i - 1) * 100, "Return" => 550, _ => 320 };
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

    private void Label(DrawingContext dc, string text, Point p, double size, Brush? brush = null, double? maxWidth = null)
    {
        var label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            size, brush ?? Ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (maxWidth is double width)
        {
            label.MaxTextWidth = width;
            label.MaxLineCount = 1;
            label.Trimming = TextTrimming.CharacterEllipsis;
        }
        dc.DrawText(label, p);
    }

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
