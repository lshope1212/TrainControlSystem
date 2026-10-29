using CTC.Core.Exceptions;
using CTC.Core.Models;
using CTC.Core.Scheduling;

namespace CTC.Core.Dispatching;

/// <summary>
/// Timing rules for a dispatched train's Active Movement Plan (its remaining
/// <see cref="DispatchedTrainState.Stops"/>): building it from the schedule, validating it,
/// planning the speed of each movement, and the <c>Arrived → Dwelling → EnRoute</c> phase changes.
/// </summary>
/// <remarks>
/// <para>
/// Every stop is <c>ARRIVE → dwell → DEPART → travel to the next stop</c>:
/// <c>Departure[n] = Arrival[n] + Dwell[n]</c> and
/// <c>AvailableTravelTime[n → n+1] = Arrival[n+1] - Departure[n]</c>. Dwell never moves the
/// arrival at the same stop; it only shortens the time left for the next movement, so the
/// speed of that movement is planned from the previous DEPARTURE, not the previous arrival.
/// </para>
/// <para>
/// Routing and authority are unchanged: routes come from the schedule or
/// <see cref="RouteManager"/>, authority from <see cref="AuthorityManager"/>. Nothing here
/// sends messages; <see cref="Services.CTCService"/> does that.
/// </para>
/// </remarks>
public static class MovementPlanner
{
    /// <summary>
    /// The plan a train starts with: every timed block of its schedule after the route start,
    /// with the scheduled arrival and dwell, indexed into the scheduled route.
    /// </summary>
    public static List<MovementPlanStop> CreateStops(ScheduledTrain train)
    {
        ArgumentNullException.ThrowIfNull(train);

        var stops = new List<MovementPlanStop>();
        for (int i = 1; i < train.Route.Count; i++)
        {
            var block = train.Route[i];
            if (block.ArrivalTime is { } arrival)
            {
                stops.Add(new MovementPlanStop
                {
                    BlockId = block.BlockId,
                    ArrivalTime = arrival,
                    DwellTime = block.DwellTime,
                    RouteIndex = i,
                });
            }
        }

        return stops;
    }

    /// <summary>
    /// Checks the stops' times are consistent: no negative dwell, and each stop is entered
    /// AFTER the train departs the previous one (its arrival plus dwell).
    /// </summary>
    /// <returns>A dispatcher-readable reason the plan is impossible, or null.</returns>
    public static string? ValidateStopTiming(string trainId, IReadOnlyList<MovementPlanStop> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);

        foreach (var stop in stops)
        {
            if (DwellTimePolicy.Validate(stop.DwellTime) is { } dwellError)
            {
                return $"{TrainIds.DisplayName(trainId)} at {stop.BlockId}: {dwellError}";
            }
        }

        for (int i = 1; i < stops.Count; i++)
        {
            var previous = stops[i - 1];
            var next = stops[i];
            if (SpeedPlanner.AvailableTravelTime(previous.DepartureTime, next.ArrivalTime) > TimeSpan.Zero)
            {
                continue;
            }

            return previous.DwellTime > TimeSpan.Zero
                ? SpeedPlanner.DwellConflictMessage(trainId, previous.BlockId, previous.DwellTime, previous.DepartureTime, next.BlockId, next.ArrivalTime)
                : $"{TrainIds.DisplayName(trainId)} must enter {next.BlockId} later than it enters {previous.BlockId}. Times must increase along the route.";
        }

        return null;
    }

    /// <summary>
    /// When the train can start the movement AFTER stop <paramref name="stopIndex"/>: for the
    /// stop it is in, its actual arrival plus dwell; otherwise the planned departure.
    /// </summary>
    public static TimeSpan DepartureTimeAfter(DispatchedTrainState train, int stopIndex)
    {
        ArgumentNullException.ThrowIfNull(train);

        return stopIndex == 0 && train.ReadyToDepartTime is { } ready ? ready : train.Stops[stopIndex].DepartureTime;
    }

    /// <summary>
    /// The line's blocks for <see cref="DispatchedTrainState.ActiveRouteBlockIds"/>[<paramref name="start"/>..<paramref name="end"/>]
    /// (inclusive); null when a block is no longer on the line or the range is invalid.
    /// </summary>
    public static List<CtcBlockState>? ResolveRoute(DispatchedTrainState train, CtcLineState line, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(train);
        ArgumentNullException.ThrowIfNull(line);

        if (start < 0 || end >= train.ActiveRouteBlockIds.Count || start > end)
        {
            return null;
        }

        var route = new List<CtcBlockState>();
        for (int i = start; i <= end; i++)
        {
            var block = line.Blocks.FirstOrDefault(block => block.BlockId == train.ActiveRouteBlockIds[i]);
            if (block is null)
            {
                return null;
            }

            route.Add(block);
        }

        return route;
    }

    /// <summary>
    /// Recalculates the planned speed of every movement the train has not started yet: the
    /// movement into stop n+1 is planned from <see cref="DepartureTimeAfter"/> stop n (or now,
    /// if later) to stop n+1's arrival. The movement already under way (to the current stop,
    /// while en route with its suggestion sent) keeps the speed CTC sent.
    /// </summary>
    /// <returns>Dispatcher-readable warnings for movements that cannot be made on time.</returns>
    public static IReadOnlyList<string> RecalculatePlannedSpeeds(DispatchedTrainState train, CtcLineState line, TimeSpan systemTime)
    {
        ArgumentNullException.ThrowIfNull(train);
        ArgumentNullException.ThrowIfNull(line);

        var warnings = new List<string>();
        for (int i = 0; i < train.Stops.Count; i++)
        {
            var stop = train.Stops[i];
            SpeedSuggestion speed;
            try
            {
                if (i == 0)
                {
                    if (train.Phase != TrainMovementPhase.EnRoute || !train.IsLegSuggestionPending)
                    {
                        // Already under way with a sent suggestion, or already reached.
                        continue;
                    }

                    var current = ResolveRoute(train, line, train.ActiveRouteIndex, stop.RouteIndex);
                    if (current is null || current.Count < 2)
                    {
                        continue;
                    }

                    speed = SpeedPlanner.CalculateSpeedToTarget(current, stop.ArrivalTime, systemTime);
                }
                else
                {
                    var leg = ResolveRoute(train, line, train.Stops[i - 1].RouteIndex, stop.RouteIndex);
                    if (leg is null || leg.Count < 2)
                    {
                        continue;
                    }

                    speed = SpeedPlanner.CalculateLegSpeed(leg, DepartureTimeAfter(train, i - 1), stop.ArrivalTime, systemTime);
                }
            }
            catch (DispatchException ex)
            {
                stop.TimingWarning = ex.Message;
                warnings.Add($"For {TrainIds.DisplayName(train.TrainId)}, {ex.Message}");
                continue;
            }

            stop.PlannedSpeedMetersPerSecond = speed.SpeedMetersPerSecond;
            stop.TimingWarning = speed.ArrivalWarning;
            if (speed.ArrivalWarning is { } warning)
            {
                warnings.Add($"For {TrainIds.DisplayName(train.TrainId)}, {warning}");
            }
        }

        return warnings;
    }

    /// <summary>
    /// Updates the plan after <see cref="Tracking.TrainLocationTracker"/> moved the train into
    /// its next route block at <paramref name="systemTime"/>. Entering the current stop makes it
    /// <see cref="TrainMovementPhase.Dwelling"/> (dwell above zero),
    /// <see cref="TrainMovementPhase.Arrived"/> (no dwell; it proceeds on the next SystemTime
    /// update) or <see cref="TrainMovementPhase.Completed"/> (final stop, no dwell). A train seen
    /// moving beyond a stop it was still in has evidently departed it: the stop is dropped and
    /// the suggestion for the next movement is marked pending.
    /// </summary>
    /// <returns>True when the phase or current stop changed.</returns>
    public static bool ApplyTrainAdvanced(DispatchedTrainState train, TimeSpan systemTime)
    {
        ArgumentNullException.ThrowIfNull(train);

        bool changed = false;
        while (train.Stops.Count > 1 && train.Phase != TrainMovementPhase.EnRoute && train.ActiveRouteIndex > train.Stops[0].RouteIndex)
        {
            train.Stops.RemoveAt(0);
            train.Phase = TrainMovementPhase.EnRoute;
            train.ActualArrivalTime = null;
            train.IsLegSuggestionPending = true;
            changed = true;
        }

        if (train.Phase == TrainMovementPhase.EnRoute && train.CurrentStop is { } stop && train.ActiveRouteIndex == stop.RouteIndex)
        {
            train.ActualArrivalTime = systemTime;
            train.IsLegSuggestionPending = false;
            train.Phase = PhaseAtStop(train, systemTime);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Ends a dwell whose time has come: the train becomes <see cref="TrainMovementPhase.Arrived"/>
    /// (ready to proceed) or, at its final stop, <see cref="TrainMovementPhase.Completed"/>.
    /// </summary>
    /// <returns>True when the phase changed.</returns>
    public static bool CompleteDwellIfDue(DispatchedTrainState train, TimeSpan systemTime)
    {
        ArgumentNullException.ThrowIfNull(train);

        if (train.Phase != TrainMovementPhase.Dwelling)
        {
            return false;
        }

        var phase = PhaseAtStop(train, systemTime);
        if (phase == train.Phase)
        {
            return false;
        }

        train.Phase = phase;
        return true;
    }

    /// <summary>
    /// Phase of a train that is in its current stop block: dwelling until its actual arrival
    /// plus dwell, then ready to proceed (or completed at the final stop).
    /// </summary>
    public static TrainMovementPhase PhaseAtStop(DispatchedTrainState train, TimeSpan systemTime)
    {
        ArgumentNullException.ThrowIfNull(train);

        if (train.ReadyToDepartTime is { } ready && systemTime < ready)
        {
            return TrainMovementPhase.Dwelling;
        }

        return train.Stops.Count > 1 ? TrainMovementPhase.Arrived : TrainMovementPhase.Completed;
    }
}
