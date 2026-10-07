using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TrainController.Integration.Presentation;

namespace TrainController.Wpf.Views.Controls;

/// <summary>
/// Horizontal distance-guidance bar for the Main UI:
/// TRAIN ──── BRAKE POINT ──── AUTHORITY END ──── STATION (positions to scale, in feet).
/// Pure rendering of a <see cref="GuidanceBarModel"/>; all values are computed by the controller.
/// Colors come from the theme, so restyling only touches Theme.xaml.
/// </summary>
public sealed class GuidanceBar : FrameworkElement
{
    public static readonly DependencyProperty ModelProperty =
        DependencyProperty.Register(
            nameof(Model),
            typeof(GuidanceBarModel),
            typeof(GuidanceBar),
            new FrameworkPropertyMetadata(GuidanceBarModel.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Minimum span drawn, so short distances are not stretched across the whole bar.</summary>
    private const double MinimumSpanFeet = 500.0;

    private const double SideMargin = 36.0;

    public GuidanceBar()
    {
        MinHeight = 96;
        SnapsToDevicePixels = true;
    }

    public GuidanceBarModel Model
    {
        get => (GuidanceBarModel)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 2 * SideMargin + 10 || height < 40)
        {
            return;
        }

        var model = Model ?? GuidanceBarModel.Empty;
        var trackY = Math.Round(height * 0.5) + 0.5;
        var left = SideMargin;
        var right = width - SideMargin;

        dc.DrawRectangle(ThemeBrush("PanelBrush", Brushes.White), null, new Rect(0, 0, width, height));
        dc.DrawLine(new Pen(ThemeBrush("TrackBrush", Brushes.Gray), 2), new Point(left, trackY), new Point(right, trackY));

        if (!model.HasData)
        {
            DrawText(dc, "No controller output for this train yet.", width / 2, trackY - 26, ThemeBrush("MutedTextBrush", Brushes.Gray), 12, center: true);
            DrawTrain(dc, left, trackY);
            return;
        }

        var span = new[] { model.StationFeet ?? 0, model.AuthorityEndFeet ?? 0, model.BrakePointFeet ?? 0, MinimumSpanFeet }.Max() * 1.08;
        double X(double feet) => left + Math.Clamp(feet / span, 0.0, 1.0) * (right - left);

        // Station: platform bar below the track (like the wireframe station markers).
        if (model.StationFeet is double stationFeet)
        {
            var x = X(stationFeet);
            var platform = ThemeBrush("PlatformBrush", Brushes.DimGray);
            dc.DrawRectangle(platform, null, new Rect(x - 24, trackY + 8, 48, 4));
            var name = string.IsNullOrEmpty(model.StationName) ? "Station" : model.StationName;
            DrawText(dc, $"{name}  {stationFeet.ToString("N0", CultureInfo.InvariantCulture)} ft", x, trackY + 16, ThemeBrush("TextBrush", Brushes.Black), 11, center: true);
        }

        // Authority end: red stop line.
        if (model.AuthorityEndFeet is double authorityFeet)
        {
            var x = Math.Round(X(authorityFeet)) + 0.5;
            var danger = ThemeBrush("DangerBrush", Brushes.Red);
            dc.DrawLine(new Pen(danger, 3), new Point(x, trackY - 14), new Point(x, trackY + 14));
            DrawText(dc, $"Authority end  {authorityFeet.ToString("N0", CultureInfo.InvariantCulture)} ft", x, trackY - 40, danger, 11, center: true);
        }

        // Latest brake point: orange diamond on the track.
        if (model.BrakePointFeet is double brakeFeet)
        {
            var x = X(brakeFeet);
            var warning = ThemeBrush("WarningBrush", Brushes.Orange);
            var diamond = new StreamGeometry();
            using (var ctx = diamond.Open())
            {
                ctx.BeginFigure(new Point(x, trackY - 7), true, true);
                ctx.LineTo(new Point(x + 7, trackY), true, false);
                ctx.LineTo(new Point(x, trackY + 7), true, false);
                ctx.LineTo(new Point(x - 7, trackY), true, false);
            }

            diamond.Freeze();
            dc.DrawGeometry(warning, null, diamond);
            DrawText(dc, model.BrakingDue ? "BRAKE NOW" : "Brake point", x, trackY - 24, warning, 11, center: true, bold: model.BrakingDue);
        }

        DrawTrain(dc, left, trackY);
    }

    private void DrawTrain(DrawingContext dc, double x, double trackY)
    {
        var info = ThemeBrush("InfoBrush", Brushes.SteelBlue);
        dc.DrawRoundedRectangle(info, null, new Rect(x - 22, trackY - 8, 44, 16), 3, 3);
        DrawText(dc, "TRAIN", x, trackY - 7, Brushes.White, 10, center: true, bold: true);
    }

    private void DrawText(DrawingContext dc, string text, double x, double y, Brush brush, double size, bool center, bool bold = false)
    {
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var originX = center ? x - formatted.Width / 2 : x;
        originX = Math.Clamp(originX, 0, Math.Max(0, ActualWidth - formatted.Width));
        dc.DrawText(formatted, new Point(originX, y));
    }

    private Brush ThemeBrush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
}
