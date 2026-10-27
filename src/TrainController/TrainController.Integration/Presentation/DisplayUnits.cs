using System.Globalization;
using TrainControl.Common.Utilities;

namespace TrainController.Integration.Presentation;

/// <summary>
/// The UI unit boundary: SI values in, operator-friendly imperial text out (and imperial
/// input back to SI). UI units: mph, ft, °F, hp. Internal units: m/s, m, °C, W, s. All conversions delegate to <see cref="UnitConversion"/>; no view model
/// writes its own formula.
/// </summary>
public static class DisplayUnits
{
    public const string Missing = "—";

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static double ToMph(double metersPerSecond) => UnitConversion.MetersPerSecondToMilesPerHour(metersPerSecond);

    public static double FromMph(double milesPerHour) => UnitConversion.MilesPerHourToMetersPerSecond(milesPerHour);

    public static double ToFeet(double meters) => UnitConversion.MetersToFeet(meters);

    public static double FromFeet(double feet) => UnitConversion.FeetToMeters(feet);

    public static double ToFahrenheit(double celsius) => UnitConversion.CelsiusToFahrenheit(celsius);

    public static double FromFahrenheit(double fahrenheit) => UnitConversion.FahrenheitToCelsius(fahrenheit);

    public static string Speed(double? metersPerSecond) =>
        metersPerSecond is double v ? string.Format(Culture, "{0:F1} mph", ToMph(v)) : Missing;

    public static string Distance(double? meters) =>
        meters is double d ? string.Format(Culture, "{0:N0} ft", ToFeet(d)) : Missing;

    public static double ToHorsepower(double watts) => UnitConversion.WattsToHorsepower(watts);

    /// <summary>
    /// Power in horsepower (480 000 W ≈ 643.7 hp). Values below 1 hp keep three decimals so
    /// that small non-zero power (e.g. from untuned gains) is not shown as "0.0 hp".
    /// </summary>
    public static string Power(double? watts)
    {
        if (watts is not double w)
        {
            return Missing;
        }

        var hp = ToHorsepower(w);
        return Math.Abs(hp) > 0.0 && Math.Abs(hp) < 1.0
            ? string.Format(Culture, "{0:F3} hp", hp)
            : string.Format(Culture, "{0:F1} hp", hp);
    }

    public static string Temperature(double? celsius) =>
        celsius is double c ? string.Format(Culture, "{0:F1} °F", ToFahrenheit(c)) : Missing;

    public static string SimulationTime(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0.0, seconds)).ToString(@"hh\:mm\:ss\.f", Culture);

    /// <summary>Formats an editable number for a text box (no unit suffix).</summary>
    public static string Number(double value, int decimals = 1) =>
        value.ToString("F" + decimals.ToString(Culture), Culture);

    /// <summary>Parses user-entered numbers; accepts invariant ("1.5") and current-culture input.</summary>
    public static bool TryParseNumber(string? text, out double value)
    {
        value = 0.0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return (double.TryParse(text, NumberStyles.Float, Culture, out value)
                || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            && double.IsFinite(value);
    }
}
