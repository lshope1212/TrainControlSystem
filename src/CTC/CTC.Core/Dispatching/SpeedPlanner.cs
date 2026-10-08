using System.Globalization;
using CTC.Core.Models;
using TrainControl.Common.Utilities;

namespace CTC.Core.Dispatching;

/// <summary>Initial suggested speed for a train being released; see <see cref="SpeedPlanner.CalculateInitialSpeed"/>.</summary>
/// <param name="SpeedMetersPerSecond">Suggested speed, never above the permitted maximum.</param>
/// <param name="IsLate">The train is already past its scheduled time at the target block, so it was given the permitted maximum.</param>
/// <param name="TargetBlockId">The next timed block on the route, which the speed is planned to reach on time.</param>
public readonly record struct InitialSpeedSuggestion(double SpeedMetersPerSecond, bool IsLate, string TargetBlockId);

/// <summary>
/// Speed rules for scheduled routes: checks a schedule is physically achievable, and
/// computes the initial suggested speed at dispatch. Calculations are in SI units; block
/// speed limits arrive in km/h and are converted here.
/// </summary>
/// <remarks>
/// A route is planned span by span, where a span runs from one timed block to the next and
/// may pass through untimed (routed-through) blocks. No acceleration/deceleration is
/// modeled: a train is assumed to cover a span at one constant speed, which therefore must
/// respect the lowest speed limit in the span. The <c>route</c> arguments are the train's
/// route blocks, aligned index-for-index with <see cref="ScheduledTrain.Route"/>.
/// </remarks>
public static class SpeedPlanner
{
    // Schedule times are whole seconds, so this only absorbs floating-point rounding.
    private const double ToleranceMetersPerSecond = 1e-9;

    /// <summary>
    /// Highest speed permitted while travelling over <paramref name="blocks"/>: the vehicle's
    /// maximum, and the lowest of the blocks' speed limits (so lines where the limit changes
    /// are handled conservatively).
    /// </summary>
    public static double GetMaxPermittedSpeedMetersPerSecond(params IEnumerable<CtcBlockState> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        double limitKph = blocks.Select(block => block.SpeedLimitKilometersPerHour)
            .Append(TrainPerformance.MaxSpeedKilometersPerHour)
            .Min();
        return UnitConversion.KilometersPerHourToMetersPerSecond(Math.Max(0.0, limitKph));
    }

    /// <summary>
    /// Checks every span between consecutive timed blocks: covering the span's distance (the
    /// lengths of the blocks from the span's first block up to, not including, the block it
    /// ends by entering) in the scheduled time must not need more than the permitted maximum.
    /// An impossible schedule is rejected, never silently clamped.
    /// </summary>
    /// <returns>A dispatcher-readable reason the schedule is infeasible, or null when it is feasible.</returns>
    public static string? ValidateFeasibility(ScheduledTrain train, IReadOnlyList<CtcBlockState> route)
    {
        ArgumentNullException.ThrowIfNull(train);
        ArgumentNullException.ThrowIfNull(route);
        EnsureAligned(train, route);

        if (route.FirstOrDefault(block => block.SpeedLimitKilometersPerHour <= 0) is { } unlimited)
        {
            return $"{train.TrainId} cannot be scheduled through {unlimited.BlockId}: the track layout gives it no speed limit.";
        }

        int spanStart = 0;
        for (int end = 1; end < route.Count; end++)
        {
            if (!train.Route[end].IsTimed)
            {
                continue;
            }

            var from = route[spanStart];
            var to = route[end];
            double distance = DistanceMeters(route, spanStart, end);
            double travelSeconds = (train.Route[end].ArrivalTime!.Value - train.Route[spanStart].ArrivalTime!.Value).TotalSeconds;

            if (distance <= 0)
            {
                // Nothing to cross, so any non-negative time works.
                if (travelSeconds < 0)
                {
                    return $"{train.TrainId} is scheduled to enter {to.BlockId} before it enters {from.BlockId}.";
                }
            }
            else if (travelSeconds <= 0)
            {
                return $"{train.TrainId} must enter {to.BlockId} later than it enters {from.BlockId}.";
            }
            else
            {
                double requiredSpeed = distance / travelSeconds;
                double maxSpeed = GetMaxPermittedSpeedMetersPerSecond(Span(route, spanStart, end));
                if (requiredSpeed > maxSpeed + ToleranceMetersPerSecond)
                {
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} cannot travel from {1} to {2} in the scheduled time. Required: {3:F1} km/h. Maximum allowed: {4:F1} km/h.",
                        train.TrainId,
                        from.BlockId,
                        to.BlockId,
                        UnitConversion.MetersPerSecondToKilometersPerHour(requiredSpeed),
                        UnitConversion.MetersPerSecondToKilometersPerHour(maxSpeed));
                }
            }

            spanStart = end;
        }

        return null;
    }

    /// <summary>
    /// Speed that gets the train from the start of its route to its NEXT timed block on
    /// schedule, measured from the actual release time (which can be after the planned
    /// departure if the clock jumped): <c>min(distance / (nextTimedArrival - systemTime), permittedMax)</c>.
    /// If that time has already passed the train is late, and gets the permitted maximum
    /// rather than an infinite or negative speed.
    /// </summary>
    public static InitialSpeedSuggestion CalculateInitialSpeed(ScheduledTrain train, IReadOnlyList<CtcBlockState> route, TimeSpan systemTime)
    {
        ArgumentNullException.ThrowIfNull(train);
        ArgumentNullException.ThrowIfNull(route);
        EnsureAligned(train, route);

        int target = Enumerable.Range(1, Math.Max(0, route.Count - 1)).FirstOrDefault(i => train.Route[i].IsTimed);
        if (target == 0)
        {
            throw new InvalidOperationException($"{train.TrainId} has no next timed block to plan a speed toward.");
        }

        string targetBlockId = route[target].BlockId;
        double maxSpeed = GetMaxPermittedSpeedMetersPerSecond(Span(route, 0, target));
        double remainingSeconds = (train.Route[target].ArrivalTime!.Value - systemTime).TotalSeconds;

        if (remainingSeconds <= 0)
        {
            return new InitialSpeedSuggestion(maxSpeed, IsLate: true, targetBlockId);
        }

        double requiredSpeed = DistanceMeters(route, 0, target) / remainingSeconds;
        return new InitialSpeedSuggestion(Math.Min(requiredSpeed, maxSpeed), IsLate: false, targetBlockId);
    }

    /// <summary>Blocks <paramref name="start"/> through <paramref name="end"/>, inclusive.</summary>
    private static IEnumerable<CtcBlockState> Span(IReadOnlyList<CtcBlockState> route, int start, int end) =>
        route.Skip(start).Take(end - start + 1);

    /// <summary>Distance from the start of block <paramref name="start"/> to the start of block <paramref name="end"/>.</summary>
    private static double DistanceMeters(IReadOnlyList<CtcBlockState> route, int start, int end) =>
        route.Skip(start).Take(end - start).Sum(block => block.LengthMeters);

    private static void EnsureAligned(ScheduledTrain train, IReadOnlyList<CtcBlockState> route)
    {
        if (route.Count != train.Route.Count)
        {
            throw new ArgumentException("The route must have one block per scheduled route block.", nameof(route));
        }
    }
}
