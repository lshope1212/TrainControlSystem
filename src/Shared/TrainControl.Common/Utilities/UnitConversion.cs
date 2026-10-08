namespace TrainControl.Common.Utilities;

/// <summary>
/// Generic unit conversion helpers shared by all subsystems.
/// </summary>
/// <remarks>
/// Convention for the whole system: calculations and inter-module messages use SI
/// (m, m/s, m/s², W, s, °C). Imperial units are a presentation concern only; convert
/// at the UI boundary with these helpers instead of writing formulas in ViewModels.
/// </remarks>
public static class UnitConversion
{
    public const double MetersPerMile = 1609.344;

    public const double MetersPerFoot = 0.3048;

    public const double SecondsPerHour = 3600.0;

    public const double MetersPerKilometer = 1000.0;

    public const double WattsPerKilowatt = 1000.0;

    /// <summary>Mechanical (imperial) horsepower: 550 ft·lbf/s = 745.699872 W.</summary>
    public const double WattsPerHorsepower = 745.699872;

    public static double MetersPerSecondToMilesPerHour(double metersPerSecond) =>
        metersPerSecond / MetersPerMile * SecondsPerHour;

    public static double MilesPerHourToMetersPerSecond(double milesPerHour) =>
        milesPerHour * MetersPerMile / SecondsPerHour;

    public static double KilometersPerHourToMetersPerSecond(double kilometersPerHour) =>
        kilometersPerHour * MetersPerKilometer / SecondsPerHour;

    public static double MetersPerSecondToKilometersPerHour(double metersPerSecond) =>
        metersPerSecond / MetersPerKilometer * SecondsPerHour;

    public static double MetersToFeet(double meters) => meters / MetersPerFoot;

    public static double FeetToMeters(double feet) => feet * MetersPerFoot;

    public static double CelsiusToFahrenheit(double celsius) => celsius * 9.0 / 5.0 + 32.0;

    public static double FahrenheitToCelsius(double fahrenheit) => (fahrenheit - 32.0) * 5.0 / 9.0;

    public static double WattsToKilowatts(double watts) => watts / WattsPerKilowatt;

    public static double KilowattsToWatts(double kilowatts) => kilowatts * WattsPerKilowatt;

    public static double WattsToHorsepower(double watts) => watts / WattsPerHorsepower;

    public static double HorsepowerToWatts(double horsepower) => horsepower * WattsPerHorsepower;
}
