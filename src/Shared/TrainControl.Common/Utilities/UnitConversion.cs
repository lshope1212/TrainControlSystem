namespace TrainControl.Common.Utilities;

/// <summary>
/// Metric (internal SI) to imperial (display) conversions, and a few helpers used
/// at the UI boundary. The whole system computes in SI; these functions exist so
/// UIs can DISPLAY imperial without leaking imperial into the domain or the wire.
/// </summary>
public static class UnitConversion
{
    public const double MetersPerSecondToMph = 2.2369362920544;
    public const double MetersToFeet = 3.28083989501312;
    public const double KilogramsToPounds = 2.20462262185;
    public const double WattsToHorsepower = 1.0 / 745.699871582;

    public static double MetersPerSecondToMilesPerHour(double metersPerSecond) =>
        metersPerSecond * MetersPerSecondToMph;

    /// <summary>Acceleration m/s^2 -> mph per second (the UI's accel unit).</summary>
    public static double MetersPerSecondSquaredToMphPerSecond(double metersPerSecondSquared) =>
        metersPerSecondSquared * MetersPerSecondToMph;

    public static double MetersToFeetValue(double meters) => meters * MetersToFeet;

    public static double KilogramsToPoundsValue(double kilograms) => kilograms * KilogramsToPounds;

    public static double WattsToHorsepowerValue(double watts) => watts * WattsToHorsepower;

    public static double CelsiusToFahrenheit(double celsius) => celsius * 9.0 / 5.0 + 32.0;
}
