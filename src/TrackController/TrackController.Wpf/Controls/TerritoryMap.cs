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
    public static readonly DependencyProperty BlocksProperty = DependencyProperty.Register(nameof(Blocks), typeof(IEnumerable), typeof(TerritoryMap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
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

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        _hitTargets.Clear();
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        var blocks = Blocks?.Cast<WaysideBlockStatus>().ToArray() ?? [];
        if (blocks.Length == 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        double width = blocks.Max(blockStatus => blockStatus.MapX) + 140, height = blocks.Max(blockStatus => blockStatus.MapY) + 105;
        double scale = Math.Min(ActualWidth / width, ActualHeight / height);
        double ox = (ActualWidth - width * scale) / 2, oy = (ActualHeight - height * scale) / 2;
        Point PointAt(double mapX, double mapY) => new(ox + mapX * scale, oy + mapY * scale);
        foreach (var blockStatus in blocks)
        {
            foreach (string next in blockStatus.NextBlockIds)
            {
                var end = blocks.FirstOrDefault(blockStatus => blockStatus.BlockId == next);
                if (end is not null)
                {
                    dc.DrawLine(new Pen(Color("#9BA8B8"), 8 * scale), PointAt(blockStatus.MapX + 90, blockStatus.MapY + 18), PointAt(end.MapX, end.MapY + 18));
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

            DrawText(blockStatus.BlockId, PointAt(blockStatus.MapX + 40, blockStatus.MapY + 47), 13 * scale, Color("#253140"));
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
                    SignalState.Yellow => Brushes.Goldenrod,
                    _ => Brushes.Gray
                };
                dc.DrawEllipse(signal, null, PointAt(blockStatus.MapX + 8, blockStatus.MapY - 14), 5 * scale, 5 * scale);
                DrawText(blockStatus.IsFresh ? blockStatus.ReportedSignal.ToString() : "Unknown", PointAt(blockStatus.MapX + 20, blockStatus.MapY - 23), 11 * scale, Color("#253140"));
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

        void DrawText(string text, Point point, double size, Brush brush) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), Math.Max(8, size), brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), point);
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
