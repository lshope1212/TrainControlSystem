namespace TrainController.Core.Safety;

/// <summary>
/// Safety-critical: the train must not exceed min(authorized speed, vehicle maximum).
/// </summary>
/// <remarks>
/// Applies the service brake whenever the actual speed is above the limit. No tolerance is
/// added because no project source defines one. A driver-requested speed below the limit is
/// NOT enforced by braking (Manual mode coasts down to it; the driver may brake).
/// </remarks>
public static class OverspeedProtection
{
    public static bool IsOverspeed(double actualSpeedMetersPerSecond, double speedLimitMetersPerSecond) =>
        actualSpeedMetersPerSecond > speedLimitMetersPerSecond;

    /// <summary>
    /// Overspeed with a tolerance band and hysteresis: starts braking above limit + tolerance,
    /// keeps braking (wasBraking) until the speed is back at or below the limit.
    /// </summary>
    public static bool IsOverspeed(double actualSpeedMetersPerSecond, double speedLimitMetersPerSecond, double toleranceMetersPerSecond, bool wasBraking) =>
        wasBraking
            ? actualSpeedMetersPerSecond > speedLimitMetersPerSecond
            : actualSpeedMetersPerSecond > speedLimitMetersPerSecond + Math.Max(0.0, toleranceMetersPerSecond);
}
