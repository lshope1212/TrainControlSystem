using System.Globalization;
using TrackController.Contracts.Messages;
using TrainControl.Contracts.Enums;

namespace TrackController.Wpf.ViewModels;

/// <summary>Formats SI values for the operator display without changing controller calculations.</summary>
public static class DisplayUnits
{
    public const double MetersPerFoot = 0.3048;
    public const double MetersPerSecondPerMph = 0.44704;

    /// <summary>Converts a distance in metres to formatted feet for the operator display.</summary>
    public static string FormatFeet(double value) => (value / MetersPerFoot).ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Converts speed in metres per second to formatted miles per hour for display.</summary>
    public static string FormatMph(double value) => (value / MetersPerSecondPerMph).ToString("0.#", CultureInfo.CurrentCulture);
}
