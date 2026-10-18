using System.Globalization;
using CTC.Core.Exceptions;
using CTC.Core.Models;
using TrainControl.Common.Utilities;

namespace CTC.Core.Dispatching;

/// <summary>
/// Suggested speed toward a target block; see <see cref="SpeedPlanner.CalculateInitialSpeed"/>
/// and <see cref="SpeedPlanner.CalculateSpeedToTarget"/>.
/// </summary>
/// <param name="SpeedMetersPerSecond">Suggested speed, never above the permitted maximum.</param>
/// <param name="IsLate">The target time has already passed, so the train was given the permitted maximum.</param>
/// <param name="IsCapped">Reaching the target on time would need more than the permitted maximum, so the train was given the permitted maximum.</param>
/// <param name="TargetBlockId">The block the speed is planned to reach on time.</param>
/// <param name="TargetArrivalTime">Time of simulation day the train should enter <paramref name="TargetBlockId"/>.</param>
/// <param name="RequiredSpeedMetersPerSecond">Speed that would reach the target exactly on time; infinite when <paramref name="IsLate"/>.</param>
/// <param name="MaxPermittedSpeedMetersPerSecond">Highest speed permitted on the way to the target.</param>
public readonly record struct SpeedSuggestion(
    double SpeedMetersPerSecond,
    bool IsLate,
    bool IsCapped,
    string TargetBlockId,
    TimeSpan TargetArrivalTime,
    double RequiredSpeedMetersPerSecond,
    double MaxPermittedSpeedMetersPerSecond)
{
    /// <summary>
    /// Dispatcher-readable reason the target time cannot be met (starting lower-case, so a
    /// caller can prefix the train's name), or null when it can.
    /// </summary>
    public string? ArrivalWarning
    {
        get
        {
            if (IsLate)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    @"the requested arrival at {0} ({1:hh\:mm\:ss}) has already passed; suggested the maximum permitted speed ({2:F1} km/h).",
                    TargetBlockId,
                    TargetArrivalTime,
                    UnitConversion.MetersPerSecondToKilometersPerHour(MaxPermittedSpeedMetersPerSecond));
            }

            if (IsCapped)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    @"the requested arrival at {0} ({1:hh\:mm\:ss}) cannot be met. Required: {2:F1} km/h. Maximum allowed: {3:F1} km/h. Suggested the maximum permitted speed.",
                    TargetBlockId,
                    TargetArrivalTime,
                    UnitConversion.MetersPerSecondToKilometersPerHour(RequiredSpeedMetersPerSecond),
                    UnitConversion.MetersPerSecondToKilometersPerHour(MaxPermittedSpeedMetersPerSecond));
            }

            return null;
        }
    }
}

/// <summary>
/// Speed rules for scheduled routes: checks a schedule is physically achievable, and
/// computes the suggested speed at dispatch and when the dispatcher re-plans a released
/// train. Calculations are in SI units; block
/// speed limits arrive in km/h and are converted here.
/// </summary>
/// <remarks>
/// A route is planned span by span, where a span runs from one timed block to the next and
/// may pass through untimed (routed-through) blocks. No acceleration/deceleration is
/// modeled: a train is assumed to cover a span at one constant speed, which therefore must
/// respect the lowest speed limit in the span. Where a <see cref="ScheduledTrain"/> is also
/// passed, the <c>route</c> argument is the train's route blocks, aligned index-for-index
/// with <see cref="ScheduledTrain.Route"/>.
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
            return $"{TrainIds.DisplayName(train.TrainId)} cannot be scheduled through {unlimited.BlockId}: the track layout gives it no speed limit.";
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
                    return $"{TrainIds.DisplayName(train.TrainId)} is scheduled to enter {to.BlockId} before it enters {from.BlockId}.";
                }
            }
            else if (travelSeconds <= 0)
            {
                return $"{TrainIds.DisplayName(train.TrainId)} must enter {to.BlockId} later than it enters {from.BlockId}.";
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
                        TrainIds.DisplayName(train.TrainId),
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
    public static SpeedSuggestion CalculateInitialSpeed(ScheduledTrain train, IReadOnlyList<CtcBlockState> route, TimeSpan systemTime)
    {
        ArgumentNullException.ThrowIfNull(train);
        ArgumentNullException.ThrowIfNull(route);
        EnsureAligned(train, route);

        int target = Enumerable.Range(1, Math.Max(0, route.Count - 1)).FirstOrDefault(i => train.Route[i].IsTimed);
        if (target == 0)
        {
            throw new InvalidOperationException($"{TrainIds.DisplayName(train.TrainId)} has no next timed block to plan a speed toward.");
        }

        return PlanSpeed(route, target, train.Route[target].ArrivalTime!.Value, systemTime);
    }

    /// <summary>
    /// Speed that gets an already-released train along <paramref name="route"/> (starting with
    /// the block the train is in, ending with its target block) into the target at
    /// <paramref name="targetArrivalTime"/>: <c>min(distance / (targetArrivalTime - systemTime), permittedMax)</c>.
    /// When that time cannot be met (it has passed, or it would need more than the permitted
    /// maximum) the train gets the permitted maximum and <see cref="SpeedSuggestion.ArrivalWarning"/>
    /// says why. A track or vehicle limit is never exceeded.
    /// </summary>
    /// <exception cref="ArgumentException">The route has fewer than two blocks.</exception>
    /// <exception cref="DispatchException">A block on the route has no speed limit.</exception>
    public static SpeedSuggestion CalculateSpeedToTarget(IReadOnlyList<CtcBlockState> route, TimeSpan targetArrivalTime, TimeSpan systemTime)
    {
        ArgumentNullException.ThrowIfNull(route);

        if (route.Count < 2)
        {
            throw new ArgumentException("The route must run from the train's block to a different target block.", nameof(route));
        }

        // A missing limit would make the permitted maximum 0; refuse rather than suggest a stop.
        if (route.FirstOrDefault(block => block.SpeedLimitKilometersPerHour <= 0) is { } unlimited)
        {
            throw new DispatchException($"No speed can be planned through {unlimited.BlockId}: the track layout gives it no speed limit.");
        }

        return PlanSpeed(route, route.Count - 1, targetArrivalTime, systemTime);
    }

    /// <summary>
    /// The speed rule shared by initial and updated suggestions: one constant speed from the
    /// start of <c>route[0]</c> to the start of <c>route[target]</c>, capped at the permitted
    /// maximum over those blocks; the permitted maximum when the target time has passed.
    /// </summary>
    private static SpeedSuggestion PlanSpeed(IReadOnlyList<CtcBlockState> route, int target, TimeSpan targetArrivalTime, TimeSpan systemTime)
    {
        string targetBlockId = route[target].BlockId;
        double maxSpeed = GetMaxPermittedSpeedMetersPerSecond(Span(route, 0, target));
        double remainingSeconds = (targetArrivalTime - systemTime).TotalSeconds;

        if (remainingSeconds <= 0)
        {
            return new SpeedSuggestion(maxSpeed, IsLate: true, IsCapped: false, targetBlockId, targetArrivalTime, double.PositiveInfinity, maxSpeed);
        }

        double requiredSpeed = DistanceMeters(route, 0, target) / remainingSeconds;
        bool isCapped = requiredSpeed > maxSpeed + ToleranceMetersPerSecond;
        return new SpeedSuggestion(Math.Min(requiredSpeed, maxSpeed), IsLate: false, isCapped, targetBlockId, targetArrivalTime, requiredSpeed, maxSpeed);
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
