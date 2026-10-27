namespace TrainController.Core.Control;

/// <summary>Constant-deceleration stopping distance, SI units.</summary>
public static class StoppingDistance
{
    /// <summary>v² / (2·a). Zero for a stopped train.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="decelerationMetersPerSecondSquared"/> is not positive.</exception>
    public static double Compute(double speedMetersPerSecond, double decelerationMetersPerSecondSquared)
    {
        if (!(decelerationMetersPerSecondSquared > 0.0))
        {
            throw new ArgumentOutOfRangeException(nameof(decelerationMetersPerSecondSquared), "Deceleration must be > 0.");
        }

        return speedMetersPerSecond <= 0.0
            ? 0.0
            : speedMetersPerSecond * speedMetersPerSecond / (2.0 * decelerationMetersPerSecondSquared);
    }
}
