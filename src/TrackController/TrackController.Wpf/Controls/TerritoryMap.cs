using System.Collections;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows;
using TrackController.Contracts.Messages;
using TrainControl.Contracts.Enums;

namespace TrackController.Wpf.Controls;

/// <summary>Code-native schematic. Geometry comes from the loaded territory configuration.</summary>
public sealed class TerritoryMap : FrameworkElement
{
    public static readonly DependencyProperty BlocksProperty = DependencyProperty.Register(nameof(Blocks), typeof(IEnumerable), typeof(TerritoryMap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty SelectedBlockIdProperty = DependencyProperty.Register(nameof(SelectedBlockId), typeof(string), typeof(TerritoryMap), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public IEnumerable? Blocks
    {
        get => (IEnumerable?)GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    public string SelectedBlockId
    {
        get => (string)GetValue(SelectedBlockIdProperty);
        set => SetValue(SelectedBlockIdProperty, value);
    }

    private readonly Dictionary<string, Rect> _hitTargets = [];

    private static Brush Color(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;

    private WaysideBlockStatus[] LayoutBlocks()
    {
        var source = Blocks?.Cast<WaysideBlockStatus>().ToArray() ?? [];
        if (source.Length == 0) return [];
        // Remove empty canvas bands belonging to other territories. Keep each row's physical
        // ordering; connections still come exclusively from the actual topology.
        var rows = source.Select(block => block.MapY).Distinct().Order().Select((y, index) => (y, index))
            .ToDictionary(item => item.y, item => 65d + item.index * 135);
        double left = source.Min(block => block.MapX);
        return source.Select(block => block with { MapX = block.MapX - left + 30, MapY = rows[block.MapY] }).ToArray();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var blocks = LayoutBlocks();
        if (blocks.Length == 0) return new Size(400, 180);
        return new Size(blocks.Max(b => b.MapX) - blocks.Min(b => b.MapX) + 170,
                        blocks.Max(b => b.MapY) - blocks.Min(b => b.MapY) + 150);
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        _hitTargets.Clear();
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        var blocks = LayoutBlocks();
        if (blocks.Length == 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        // A line drawn on its own (e.g. Green of a multi-line setup) starts at the usual 30/60 margin
        // wherever its band lies on the shared canvas; maps that already start there are unchanged.
        double shiftX = Math.Max(0, blocks.Min(blockStatus => blockStatus.MapX) - 30), shiftY = Math.Max(0, blocks.Min(blockStatus => blockStatus.MapY) - 60);
        double width = blocks.Max(blockStatus => blockStatus.MapX) - shiftX + 140, height = blocks.Max(blockStatus => blockStatus.MapY) - shiftY + 105;
        double scale = 1;
        double ox = Math.Max(0, (ActualWidth - width) / 2), oy = Math.Max(0, (ActualHeight - height) / 2);
        Point PointAt(double mapX, double mapY) => new(ox + (mapX - shiftX) * scale, oy + (mapY - shiftY) * scale);
        foreach (var blockStatus in blocks)
        {
            foreach (string next in blockStatus.NextBlockIds)
            {
                var end = blocks.FirstOrDefault(blockStatus => blockStatus.BlockId == next);
                if (end is not null)
                {
                    double dx = end.MapX - blockStatus.MapX, dy = end.MapY - blockStatus.MapY;
                    Point start, finish;
                    var bends = new List<Point>();
                    if (Math.Abs(dy) < 1 && Math.Abs(dx) <= 110)
                    {
                        start = PointAt(blockStatus.MapX + (dx > 0 ? 94 : 0), blockStatus.MapY + 18);
                        finish = PointAt(end.MapX + (dx > 0 ? 0 : 94), end.MapY + 18);
                    }
                    else if (Math.Abs(dy) < 1)
                    {
                        // A long junction connection must not look as though it traverses the
                        // intervening blocks on this drawing row.
                        start = PointAt(blockStatus.MapX + 47, blockStatus.MapY);
                        finish = PointAt(end.MapX + 47, end.MapY);
                        bends.Add(PointAt(blockStatus.MapX + 47, blockStatus.MapY - 22));
                        bends.Add(PointAt(end.MapX + 47, end.MapY - 22));
                    }
                    else
                    {
                        start = PointAt(blockStatus.MapX + 47, blockStatus.MapY + (dy > 0 ? 36 : 0));
                        finish = PointAt(end.MapX + 47, end.MapY + (dy > 0 ? 0 : 36));
                        double lane = (start.Y + finish.Y) / 2;
                        bends.Add(new Point(start.X, lane));
                        bends.Add(new Point(finish.X, lane));
                    }
                    var geometry = new StreamGeometry();
                    using (var path = geometry.Open())
                    {
                        path.BeginFigure(start, false, false);
                        foreach (var bend in bends) path.LineTo(bend, true, false);
                        path.LineTo(finish, true, false);
                    }
                    dc.DrawGeometry(null, new Pen(Color("#9BA8B8"), 3), geometry);
                }
            }
        }

        foreach (var blockStatus in blocks)
        {
            var rect = new Rect(PointAt(blockStatus.MapX, blockStatus.MapY), new Size(94 * scale, 36 * scale));
            _hitTargets[blockStatus.BlockId] = new Rect(rect.X, rect.Y - 25 * scale, rect.Width, 95 * scale);
            Brush fill = !blockStatus.IsFresh || blockStatus.Occupancy == OccupancyState.Unknown ? Color("#9BA8B8") : blockStatus.Occupancy == OccupancyState.Occupied ? Color("#C84036") : Color("#368653");
            dc.DrawRectangle(fill, new Pen(Brushes.White, 2), rect);
            if (blockStatus.Maintenance != "Open")
            {
                dc.DrawRectangle(null, new Pen(Color("#BB721F"), 2), new Rect(rect.X - 3, rect.Y - 3, rect.Width + 6, rect.Height + 6));
            }

            if (blockStatus.BlockId == SelectedBlockId)
            {
                dc.DrawRectangle(null, new Pen(Color("#2D61A8"), 2), new Rect(rect.X - 6, rect.Y - 6, rect.Width + 12, rect.Height + 12));
            }

            DrawText(blockStatus.BlockId, PointAt(blockStatus.MapX + 6, blockStatus.MapY + 8), 12 * scale, Brushes.White);
            if (blockStatus.HasSwitch)
            {
                DrawText($"Switch {blockStatus.ReportedSwitch}", PointAt(blockStatus.MapX - 4, blockStatus.MapY - 33), 12 * scale, Color("#2D61A8"));
            }
            else if (blockStatus.HasSignal)
            {
                Brush signal = !blockStatus.IsFresh ? Brushes.Gray : blockStatus.ReportedSignal switch
                {
                    SignalState.Red => Color("#C84036"),
                    SignalState.Green => Color("#368653"),
                    SignalState.SuperGreen => Color("#00B86B"),
                    SignalState.Yellow => Brushes.Goldenrod,
                    _ => Brushes.Gray
                };
                dc.DrawEllipse(signal, null, PointAt(blockStatus.MapX + 8, blockStatus.MapY - 14), 5 * scale, 5 * scale);
                DrawText(blockStatus.IsFresh ? blockStatus.ReportedSignal.ToString().Replace("SuperGreen", "Super Green") : "Unknown", PointAt(blockStatus.MapX + 20, blockStatus.MapY - 23), 11 * scale, Color("#253140"));
            }

            if (!string.IsNullOrWhiteSpace(blockStatus.StationName))
            {
                DrawText(blockStatus.StationName, PointAt(blockStatus.MapX, blockStatus.MapY - 48), 11 * scale, Color("#253140"));
            }

            if (blockStatus.HasCrossing)
            {
                DrawText($"Crossing {(blockStatus.IsFresh ? blockStatus.ReportedCrossing : CrossingState.Unknown)}", PointAt(blockStatus.MapX - 2, blockStatus.MapY + 72), 11 * scale, Color("#253140"));
            }

            if (blockStatus.IsFaulted)
            {
                DrawText("FAULT", PointAt(blockStatus.MapX + 24, blockStatus.MapY + 8), 12 * scale, Brushes.White);
            }
        }

        void DrawText(string text, Point point, double size, Brush brush) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), Math.Max(8, size), brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = 100, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis }, point);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs eventArgs)
    {
        base.OnMouseLeftButtonDown(eventArgs);
        var point = eventArgs.GetPosition(this);
        foreach (var item in _hitTargets)
        {
            if (item.Value.Contains(point))
            {
                SetCurrentValue(SelectedBlockIdProperty, item.Key);
                break;
            }
        }
    }
}
