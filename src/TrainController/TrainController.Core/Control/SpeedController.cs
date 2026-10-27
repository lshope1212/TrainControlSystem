using TrainController.Abstractions.Inputs;

namespace TrainController.Core.Control;

/// <summary>
/// PI speed regulator for ONE train: speed error in m/s -> traction power in W.
/// </summary>
/// <remarks>
/// power = Kp·e + Ki·∫e dt, clamped to [0, rated power]. Integration uses the fixed
/// SIMULATED timestep from the input, never wall-clock time. Anti-windup: the integral is
/// not accumulated while the output is saturated in the direction of the error. The
/// integral is cleared whenever traction is inhibited (brakes, doors), so it cannot wind up
/// while the train is held.
/// </remarks>
public sealed class SpeedController
{
    /// <summary>∫ (target − actual) dt, in metres.</summary>
    public double IntegralMeters { get; private set; }

    public double ComputePower(
        double targetSpeedMetersPerSecond,
        double actualSpeedMetersPerSecond,
        EngineerSettings gains,
        double deltaTimeSeconds,
        double maxPowerWatts)
    {
        ArgumentNullException.ThrowIfNull(gains);

        var error = targetSpeedMetersPerSecond - actualSpeedMetersPerSecond;
        var candidateIntegral = IntegralMeters + error * deltaTimeSeconds;
        var unclamped = gains.Kp * error + gains.Ki * candidateIntegral;

        var saturatedHigh = unclamped > maxPowerWatts && error > 0.0;
        var saturatedLow = unclamped < 0.0 && error < 0.0;
        if (!saturatedHigh && !saturatedLow)
        {
            IntegralMeters = candidateIntegral;
        }

        return Math.Clamp(unclamped, 0.0, maxPowerWatts);
    }

    /// <summary>Clears the integral (traction inhibited, or controller reset).</summary>
    public void ResetIntegral() => IntegralMeters = 0.0;
}
