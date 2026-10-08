namespace TrainControl.Common.Utilities;

/// <summary>
/// Placeholder for generic unit conversion helpers shared by all subsystems.
/// </summary>
public static class UnitConversion
{
    public const double MetersPerMile = 1609.344;

    public const double MetersPerFoot = 0.3048;

    public static double MetersPerSecondToMilesPerHour(double metersPerSecond) =>
        metersPerSecond / MetersPerMile * 3600.0;

    public static double MetersToFeet(double meters) => meters / MetersPerFoot;

    public static double KilometersPerHourToMetersPerSecond(double kilometersPerHour) =>
        kilometersPerHour * 1000.0 / 3600.0;

    public static double MetersPerSecondToKilometersPerHour(double metersPerSecond) =>
        metersPerSecond * 3600.0 / 1000.0;

    public static double KilometersPerHourToMilesPerHour(double kilometersPerHour) =>
        MetersPerSecondToMilesPerHour(KilometersPerHourToMetersPerSecond(kilometersPerHour));
}
