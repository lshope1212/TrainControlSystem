using TrainController.Abstractions.Outputs;

namespace TrainController.Core.Control;

/// <summary>Effective target speed and the constraint that set it.</summary>
public readonly record struct TargetSpeed(double MetersPerSecond, TargetSpeedConstraint LimitedBy);

/// <summary>
/// Picks the effective target speed as the MINIMUM of all applicable limits and reports which
/// limit was binding. Ties keep the earlier-listed (more fundamental) constraint.
/// </summary>
public sealed class TargetSpeedSelector
{
    private double _value = double.PositiveInfinity;
    private TargetSpeedConstraint _limitedBy = TargetSpeedConstraint.None;

    public TargetSpeedSelector Limit(double metersPerSecond, TargetSpeedConstraint constraint)
    {
        var limit = Math.Max(0.0, metersPerSecond);
        if (limit < _value)
        {
            _value = limit;
            _limitedBy = constraint;
        }

        return this;
    }

    public TargetSpeed Result =>
        double.IsPositiveInfinity(_value) ? new TargetSpeed(0.0, TargetSpeedConstraint.None) : new TargetSpeed(_value, _limitedBy);

    /// <summary>
    /// Highest speed from which a constant <paramref name="decelerationMetersPerSecondSquared"/>
    /// still stops within <paramref name="distanceMeters"/>: √(2·a·d). The inverse of
    /// <see cref="StoppingDistance.Compute"/>; no extra margin.
    /// </summary>
    public static double BrakingCurveSpeed(double distanceMeters, double decelerationMetersPerSecondSquared) =>
        Math.Sqrt(2.0 * decelerationMetersPerSecondSquared * Math.Max(0.0, distanceMeters));
}
