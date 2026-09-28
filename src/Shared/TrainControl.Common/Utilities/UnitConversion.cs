namespace TrainControl.Common.Utilities;

/// <summary>
/// Placeholder for generic unit conversion helpers shared by all subsystems.
/// </summary>
public static class UnitConversion
{
    public const double MetersPerMile = 1609.344;

    public static double MetersPerSecondToMilesPerHour(double metersPerSecond) =>
        metersPerSecond / MetersPerMile * 3600.0;
}
