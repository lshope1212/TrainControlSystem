using System.Windows.Media;

namespace TrackController.Wpf.ViewModels;

/// <summary>Chooses the header color that identifies the loaded line at a glance.</summary>
public static class LineAccent
{
    private static readonly Brush Blue = Frozen("#2563EB"), Green = Frozen("#16A34A"), Red = Frozen("#DC2626"), Other = Frozen("#6B7280");

    /// <summary>Matches the line's color word; unrecognized lines stay neutral gray.</summary>
    public static Brush For(string? lineName) => lineName switch
    {
        _ when Has(lineName, "blue") => Blue,
        _ when Has(lineName, "green") => Green,
        _ when Has(lineName, "red") => Red,
        _ => Other
    };

    private static bool Has(string? text, string word) => text?.Split([' ', '-', '_', '.', ','], StringSplitOptions.RemoveEmptyEntries).Contains(word, StringComparer.OrdinalIgnoreCase) == true;

    private static Brush Frozen(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
