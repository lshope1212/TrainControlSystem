using TrainControl.Common.Utilities;

namespace CTC.Core.Dispatching;

/// <summary>
/// Physical performance of the vehicle CTC plans for: the Bombardier FLEXITY 2.
/// </summary>
/// <remarks>
/// TODO: these values are hardcoded from the supplied vehicle data sheet. They should
/// eventually come from the Train Model / integrated vehicle data (possibly per train).
/// Acceleration and deceleration are deliberately not modeled yet.
/// </remarks>
public static class TrainPerformance
{
    /// <summary>Maximum physical speed of the vehicle.</summary>
    public const double MaxSpeedKilometersPerHour = 70.0;

    public static double MaxSpeedMetersPerSecond => UnitConversion.KilometersPerHourToMetersPerSecond(MaxSpeedKilometersPerHour);
}
