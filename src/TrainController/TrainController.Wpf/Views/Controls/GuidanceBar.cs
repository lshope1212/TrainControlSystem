using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TrainController.Integration.Presentation;

namespace TrainController.Wpf.Views.Controls;

/// <summary>
/// Horizontal distance-guidance bar for the Main UI:
/// TRAIN ──── station / authority BRAKE POINTS ──── AUTHORITY END ──── STATION (positions to
/// scale, in feet, measured from the train's front; every marker is a vertical line).
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

    /// <summary>Width of the TRAIN icon (symbol, not to scale).</summary>
    private const double TrainIconWidth = 44.0;

    public GuidanceBar()
    {
        MinHeight = 120;
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
        // Position 0 = the train's reference point = the FRONT of the train. The icon is drawn
        // entirely behind that point, so the brake point / authority end reach the icon's front
        // edge exactly when the controller acts on them.
        var trackStart = SideMargin;
        var left = SideMargin + TrainIconWidth / 2;
        var right = width - SideMargin;

        dc.DrawRectangle(ThemeBrush("PanelBrush", Brushes.White), null, new Rect(0, 0, width, height));
        dc.DrawLine(new Pen(ThemeBrush("TrackBrush", Brushes.Gray), 2), new Point(trackStart, trackY), new Point(right, trackY));

        if (!model.HasData)
        {
            DrawText(dc, "No controller output for this train yet.", width / 2, trackY - 26, ThemeBrush("MutedTextBrush", Brushes.Gray), 12, center: true);
            DrawTrain(dc, left, trackY);
            return;
        }

        var span = new[]
        {
            model.StationFeet ?? 0, model.AuthorityEndFeet ?? 0, model.BrakePointFeet ?? 0, model.AuthorityBrakePointFeet ?? 0, MinimumSpanFeet,
        }.Max() * 1.08;
        double X(double feet) => left + Math.Clamp(feet / span, 0.0, 1.0) * (right - left);

        // All markers are vertical lines at their exact (to-scale) position; the train's front is at 0.
        // Labels: above the track = station brake point (row 1) and authority end (row 2);
        // below the track = station (row 1) and authority brake point (row 2).

        // Station: grey vertical line.
        if (model.StationFeet is double stationFeet)
        {
            var x = Snap(X(stationFeet));
            var platform = ThemeBrush("PlatformBrush", Brushes.DimGray);
            dc.DrawLine(new Pen(platform, 3), new Point(x, trackY - 14), new Point(x, trackY + 14));
            var name = string.IsNullOrEmpty(model.StationName) ? "Station" : model.StationName;
            DrawText(dc, $"{name}  {Feet(stationFeet)}", x, trackY + 16, ThemeBrush("TextBrush", Brushes.Black), 11, center: true);
        }

        // Authority end: solid red stop line.
        if (model.AuthorityEndFeet is double authorityFeet)
        {
            var x = Snap(X(authorityFeet));
            var danger = ThemeBrush("DangerBrush", Brushes.Red);
            dc.DrawLine(new Pen(danger, 3), new Point(x, trackY - 14), new Point(x, trackY + 14));
            DrawText(dc, $"Authority end  {Feet(authorityFeet)}", x, trackY - 44, danger, 11, center: true);
        }

        // Authority brake point: dashed red line (where authority protection applies the service brake).
        if (model.AuthorityBrakePointFeet is double authorityBrakeFeet)
        {
            var x = Snap(X(authorityBrakeFeet));
            var danger = ThemeBrush("DangerBrush", Brushes.Red);
            var dashed = new Pen(danger, 2) { DashStyle = DashStyles.Dash };
            dc.DrawLine(dashed, new Point(x, trackY - 12), new Point(x, trackY + 12));
            DrawText(dc, model.AuthorityBrakingDue ? "AUTHORITY BRAKE" : "Authority brake point", x, trackY + 32, danger, 11,
                center: true, bold: model.AuthorityBrakingDue);
        }

        // Station brake point: orange line (latest point to start braking for the station).
        if (model.BrakePointFeet is double brakeFeet)
        {
            var x = Snap(X(brakeFeet));
            var warning = ThemeBrush("WarningBrush", Brushes.Orange);
            dc.DrawLine(new Pen(warning, 3), new Point(x, trackY - 12), new Point(x, trackY + 12));
            DrawText(dc, model.BrakingDue ? "BRAKE NOW" : "Station brake point", x, trackY - 28, warning, 11,
                center: true, bold: model.BrakingDue);
        }

        DrawTrain(dc, left, trackY);
    }

    /// <summary>Draws the TRAIN icon with its right (front) edge at <paramref name="front"/>.</summary>
    private void DrawTrain(DrawingContext dc, double front, double trackY)
    {
        var info = ThemeBrush("InfoBrush", Brushes.SteelBlue);
        dc.DrawRoundedRectangle(info, null, new Rect(front - TrainIconWidth, trackY - 8, TrainIconWidth, 16), 3, 3);
        DrawText(dc, "TRAIN", front - TrainIconWidth / 2, trackY - 7, Brushes.White, 10, center: true, bold: true);
    }

    private static double Snap(double x) => Math.Round(x) + 0.5;

    private static string Feet(double feet) => $"{feet.ToString("N0", CultureInfo.InvariantCulture)} ft";

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
