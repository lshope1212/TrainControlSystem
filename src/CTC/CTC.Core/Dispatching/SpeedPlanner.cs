using System.Globalization;
using CTC.Core.Models;
using TrainControl.Common.Utilities;

namespace CTC.Core.Dispatching;

/// <summary>Initial suggested speed for a train being released; see <see cref="SpeedPlanner.CalculateInitialSpeed"/>.</summary>
/// <param name="SpeedMetersPerSecond">Suggested speed, never above the permitted maximum.</param>
/// <param name="IsLate">The train is already past its scheduled time at the next block, so it was given the permitted maximum.</param>
public readonly record struct InitialSpeedSuggestion(double SpeedMetersPerSecond, bool IsLate);

/// <summary>
/// Speed rules for scheduled routes: checks a schedule is physically achievable, and
/// computes the initial suggested speed at dispatch. Calculations are in SI units; block
/// speed limits arrive in km/h and are converted here.
/// </summary>
/// <remarks>
/// No acceleration/deceleration is modeled: a train is assumed to cross a block at a
/// constant speed. The <c>route</c> arguments are the train's route blocks, aligned
/// index-for-index with <see cref="ScheduledTrain.BlockTimes"/>.
/// </remarks>
public static class SpeedPlanner
{
    // Schedule times are whole seconds, so this only absorbs floating-point rounding.
    private const double ToleranceMetersPerSecond = 1e-9;

    /// <summary>
    /// Highest speed permitted while crossing <paramref name="current"/> toward
    /// <paramref name="next"/>: the vehicle's maximum, and conservatively the lower of the two
    /// blocks' speed limits (so lines where the limit changes are handled).
    /// </summary>
    public static double GetMaxPermittedSpeedMetersPerSecond(CtcBlockState current, CtcBlockState next)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(next);

        double blockLimitKph = Math.Min(current.SpeedLimitKilometersPerHour, next.SpeedLimitKilometersPerHour);
        double limitKph = Math.Min(blockLimitKph, TrainPerformance.MaxSpeedKilometersPerHour);
        return UnitConversion.KilometersPerHourToMetersPerSecond(Math.Max(0.0, limitKph));
    }

    /// <summary>
    /// Checks every consecutive pair of scheduled blocks: crossing block i (its length) in the
    /// time between entering block i and entering block i+1 must not need more than the
    /// permitted maximum. An impossible schedule is rejected, never silently clamped.
    /// </summary>
    /// <returns>A dispatcher-readable reason the schedule is infeasible, or null when it is feasible.</returns>
    public static string? ValidateFeasibility(ScheduledTrain train, IReadOnlyList<CtcBlockState> route)
    {
        ArgumentNullException.ThrowIfNull(train);
        ArgumentNullException.ThrowIfNull(route);
        EnsureAligned(train, route);

        for (int i = 0; i + 1 < route.Count; i++)
        {
            var current = route[i];
            var next = route[i + 1];

            foreach (var block in new[] { current, next })
            {
                if (block.SpeedLimitKilometersPerHour <= 0)
                {
                    return $"{train.TrainId} cannot be scheduled through {block.BlockId}: the track layout gives it no speed limit.";
                }
            }

            double travelSeconds = (train.BlockTimes[i + 1].ArrivalTime - train.BlockTimes[i].ArrivalTime).TotalSeconds;
            if (current.LengthMeters <= 0)
            {
                // Nothing to cross, so any non-negative time works.
                if (travelSeconds < 0)
                {
                    return $"{train.TrainId} is scheduled to enter {next.BlockId} before it enters {current.BlockId}.";
                }

                continue;
            }

            if (travelSeconds <= 0)
            {
                return $"{train.TrainId} must enter {next.BlockId} later than it enters {current.BlockId}.";
            }

            double requiredSpeed = current.LengthMeters / travelSeconds;
            double maxSpeed = GetMaxPermittedSpeedMetersPerSecond(current, next);
            if (requiredSpeed > maxSpeed + ToleranceMetersPerSecond)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} cannot travel from {1} to {2} in the scheduled time. Required: {3:F1} km/h. Maximum allowed: {4:F1} km/h.",
                    train.TrainId,
                    current.BlockId,
                    next.BlockId,
                    UnitConversion.MetersPerSecondToKilometersPerHour(requiredSpeed),
                    UnitConversion.MetersPerSecondToKilometersPerHour(maxSpeed));
            }
        }

        return null;
    }

    /// <summary>
    /// Speed that gets the train across its start block by its scheduled time at the NEXT
    /// block, measured from the actual release time (which can be after the planned
    /// departure if the clock jumped): <c>min(startLength / (nextArrival - systemTime), permittedMax)</c>.
    /// If that time has already passed the train is late, and gets the permitted maximum
    /// rather than an infinite or negative speed.
    /// </summary>
    public static InitialSpeedSuggestion CalculateInitialSpeed(ScheduledTrain train, IReadOnlyList<CtcBlockState> route, TimeSpan systemTime)
    {
        ArgumentNullException.ThrowIfNull(train);
        ArgumentNullException.ThrowIfNull(route);
        EnsureAligned(train, route);

        if (route.Count < 2)
        {
            throw new InvalidOperationException($"{train.TrainId} has no next scheduled block to plan a speed toward.");
        }

        var start = route[0];
        double maxSpeed = GetMaxPermittedSpeedMetersPerSecond(start, route[1]);
        double remainingSeconds = (train.BlockTimes[1].ArrivalTime - systemTime).TotalSeconds;

        if (remainingSeconds <= 0)
        {
            return new InitialSpeedSuggestion(maxSpeed, IsLate: true);
        }

        double requiredSpeed = start.LengthMeters / remainingSeconds;
        return new InitialSpeedSuggestion(Math.Min(requiredSpeed, maxSpeed), IsLate: false);
    }

    private static void EnsureAligned(ScheduledTrain train, IReadOnlyList<CtcBlockState> route)
    {
        if (route.Count != train.BlockTimes.Count)
        {
            throw new ArgumentException("The route must have one block per scheduled block time.", nameof(route));
        }
    }
}
