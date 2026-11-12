using TrackModel.Core.Models;
using TrainControl.Common.Identity;
using TrainControl.Contracts.Messages;

namespace TrackModel.Core.Services;

/// <summary>
/// Physical dispatch and position-to-block mapping: the Track Model places a dispatched train, then
/// turns the Train Model's cumulative front position into the blocks the train physically occupies.
/// </summary>
/// <remarks>
/// <para>
/// The Train Model reports only how far the FRONT of the train has travelled since activation
/// (<see cref="TrainStateMessage.PositionMeters"/>) and its length. The Track Model owns the geometry: it
/// keeps each train's traversed path with the cumulative boundary of every block on it (from the layout's
/// real block lengths, in route order, never by block number). The front is at <c>f</c>, the rear at
/// <c>f - length</c>. The head block is the one containing the front (a block spans <c>[start, end)</c>,
/// so at <c>f = 0</c> the head is the start block); a trailing block stays occupied until the rear has
/// fully left it (<c>rear &gt;= end</c>). Rear positions below 0 are in the yard, outside the modelled
/// route: no block before the start block is ever occupied.
/// </para>
/// <para>
/// The path grows only where the track physically leads: from the block the front is in, the next block
/// is the one the track and the CURRENT switch alignment lead to (<see cref="TrackTopology.PhysicalNext"/>),
/// cross-checked against the registered route. If the switch sends the train somewhere its route does not
/// go, or a trailing switch is set against it, the train is placed where the track took it (so that block
/// is protected), reported, and faulted: its footprint is then held and no further telemetry is applied.
/// Nothing is ever invented to keep a stale route alive.
/// </para>
/// <para>
/// A report that crosses several boundaries (transport may coalesce reports) is applied as one atomic
/// step per boundary, in the order the train crossed them, each raising <see cref="OccupancyTransition"/>,
/// so no intermediate occupancy is skipped. Every report is validated as a whole before anything changes:
/// invalid, stale or impossible telemetry never moves a train or clears a block.
/// </para>
/// </remarks>
public partial class TrackService
{
    /// <summary>Simulated time per Train Model physics tick (<see cref="TrainStateMessage.Sequence"/>).</summary>
    public const double TelemetryTickSeconds = 0.1;

    /// <summary>Speed no train can exceed (the Flexity 2 maximum is 19.4 m/s); bounds the distance between two reports.</summary>
    public const double MaxPlausibleSpeedMetersPerSecond = 30.0;

    /// <summary>Slack added to the plausible distance between two reports.</summary>
    public const double PositionJumpMarginMeters = 1.0;

    /// <summary>How far past the end of its route a front may stop (a braking overshoot) and stay in the last route block.</summary>
    public const double RouteEndToleranceMeters = 1.0;

    /// <summary>The first report of a newly activated train must be within this distance of 0.</summary>
    public const double InitialReportToleranceMeters = 5.0;

    public const double MaxTrainLengthMeters = 1000.0;

    private const double Epsilon = 1e-9;

    private readonly Dictionary<string, TrainTrack> _trains = new(StringComparer.Ordinal);

    /// <summary>One event per atomic footprint step, in the order the train crossed the boundaries.</summary>
    public event EventHandler<TrackOccupancyTransitionEventArgs>? OccupancyTransition;

    /// <summary>Placement, fault, retirement and withdrawn placements, for the host to report onward.</summary>
    public event EventHandler<TrainTrackingEventArgs>? TrainTrackingChanged;

    /// <summary>Every train tracked from position telemetry (pending, active or faulted).</summary>
    public IReadOnlyList<TrainTrackingSnapshot> TrackedTrains => _trains.Values.Select(Snapshot).ToList();

    public TrainTrackingSnapshot? FindTrackedTrain(string trainId) =>
        TrainIdConvention.TryNormalize(trainId, out var id) && _trains.TryGetValue(id, out var train) ? Snapshot(train) : null;

    /// <summary>
    /// Validates a dispatch and registers the train's route, without occupying anything: the train
    /// appears on the track only when the Train Model's initial position report arrives
    /// (<see cref="ApplyTrainPosition"/>). Idempotent for a retry with the same request ID.
    /// </summary>
    public TrainPlacementResult RequestTrainPlacement(TrainDispatchRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string requestId = request.RequestId?.Trim() ?? string.Empty;
        if (requestId.Length == 0 || requestId.Length > 128)
            return Refuse("A dispatch request needs a request ID of up to 128 characters.");
        if (!TrainIdConvention.IsDispatchable(request.TrainId))
            return Refuse($"'{request.TrainId}' is not a dispatchable fleet train ID ({TrainIdConvention.DispatchableRangeText}).");
        string trainId = request.TrainId;
        if (string.IsNullOrWhiteSpace(request.LineId) || !Layout.Blocks.Any(b => b.LineId == request.LineId))
            return Refuse($"Unknown line '{request.LineId}'.");
        var route = request.RouteBlockIds ?? [];
        if (route.Count == 0 || route[0] != request.StartBlockId)
            return Refuse($"The route must start with the start block {request.StartBlockId}.");
        if (TrackTopology.ValidateRoute(FindBlock, request.LineId, route) is { } invalid)
            return Refuse(invalid);
        var start = FindBlock(request.StartBlockId)!;

        if (_trains.TryGetValue(trainId, out var existing))
        {
            if (existing.DispatchRequestId == requestId)
            {
                return existing.Status switch
                {
                    TrainTrackingStatus.PendingPlacement => new(TrainPlacementOutcome.AlreadyPending, $"Placement of train {trainId} at {start.Id} is registered; waiting for the Train Model's initial position report."),
                    TrainTrackingStatus.Active => new(TrainPlacementOutcome.AlreadyPlaced, $"Train {trainId} is already on the track for this dispatch (front in {existing.HeadBlockId})."),
                    _ => Refuse($"Train {trainId} is faulted ({existing.Fault}); retire it before dispatching it again."),
                };
            }

            if (existing.Status != TrainTrackingStatus.PendingPlacement)
                return Refuse($"Train {trainId} is already on the track (front in {existing.HeadBlockId}); a new placement would reset an operating train.");
        }
        else if (Layout.Blocks.FirstOrDefault(b => b.TrainId == trainId) is { } placed)
        {
            return placed.Id == start.Id
                ? new(TrainPlacementOutcome.AlreadyPlaced, $"Train {trainId} is already on the track at its start block {start.Id} (placed outside dispatch).")
                : Refuse($"Train {trainId} is already on the track at block {placed.Id}.");
        }

        if (StartBlockProblem(start, trainId) is { } unsafeStart)
            return Refuse(unsafeStart, retryable: true);

        if (existing is not null)
        {
            // A newer dispatch of the same train, still before placement, replaces the older one.
            _trains.Remove(trainId);
            Raise(TrainTrackingEventKind.PlacementWithdrawn, existing, $"superseded by dispatch request {requestId}");
        }

        _trains[trainId] = new TrainTrack
        {
            TrainId = trainId,
            LineId = request.LineId,
            DispatchRequestId = requestId,
            StartBlockId = start.Id,
            PlannedRoute = [.. route],
            Path = [start.Id],
            PathStart = [0],
        };
        return new(TrainPlacementOutcome.Registered, $"Placement of train {trainId} at {start.Id} registered ({route.Count}-block route); asking the Train Model to activate it.");
    }

    /// <summary>Drops a placement that has not reached the track (e.g. the activation could not be delivered). True when one was dropped.</summary>
    public bool CancelPendingPlacement(string dispatchRequestId, string reason)
    {
        var pending = _trains.Values.FirstOrDefault(t => t.DispatchRequestId == dispatchRequestId && t.Status == TrainTrackingStatus.PendingPlacement);
        if (pending is null) return false;
        _trains.Remove(pending.TrainId);
        Raise(TrainTrackingEventKind.PlacementWithdrawn, pending, reason);
        return true;
    }

    /// <summary>
    /// Applies one Train Model position report. The first report of a registered placement occupies the
    /// start block; later reports move the footprint along the traversed path.
    /// </summary>
    public TrainPositionResult ApplyTrainPosition(TrainStateMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!TrainIdConvention.TryNormalize(message.TrainId, out var trainId))
            return new(TrainPositionOutcome.Rejected, $"'{message.TrainId}' is not a canonical train ID; telemetry ignored.");
        if (!_trains.TryGetValue(trainId, out var train))
            return new(TrainPositionOutcome.Rejected, $"Train {trainId} has no Track Model placement; its telemetry is ignored until it is dispatched.", trainId);
        var reject = (string detail) => new TrainPositionResult(TrainPositionOutcome.Rejected, $"Train {trainId}: {detail}", trainId, train.DispatchRequestId);
        if (train.Status == TrainTrackingStatus.Faulted)
            return reject($"faulted ({train.Fault}); telemetry not applied.");
        if (message.DispatchRequestId != train.DispatchRequestId)
            return reject($"telemetry is for dispatch '{message.DispatchRequestId}', not the current dispatch '{train.DispatchRequestId}'.");

        double front = message.PositionMeters, length = message.TrainLengthMeters, speed = message.SpeedMetersPerSecond;
        if (!double.IsFinite(front) || !double.IsFinite(length) || !double.IsFinite(speed) || !double.IsFinite(message.AccelerationMetersPerSecondSquared))
            return reject("telemetry has a non-finite value.");
        if (front < 0)
            return reject($"position {front:0.###} m is negative (behind its start block); not applied.");
        if (length <= 0 || length > MaxTrainLengthMeters)
            return reject($"train length {length:0.###} m is not valid.");
        if (message.Sequence < 0)
            return reject("telemetry sequence is negative.");

        if (train.HasReport)
        {
            if (message.Sequence == train.LastSequence)
            {
                return Math.Abs(front - train.Front) < Epsilon && Math.Abs(length - train.Length) < Epsilon
                    ? new(TrainPositionOutcome.Duplicate, $"Train {trainId}: repeated report {message.Sequence}.", trainId, train.DispatchRequestId)
                    : reject($"a different report with the same sequence {message.Sequence}; not applied.");
            }

            if (message.Sequence < train.LastSequence)
                return reject($"stale report {message.Sequence} (already applied {train.LastSequence}); not applied.");
            if (Math.Abs(length - train.Length) > 1e-6)
                return Fault(train, $"its length changed from {train.Length:0.###} m to {length:0.###} m");
            double plausible = (message.Sequence - train.LastSequence) * TelemetryTickSeconds * MaxPlausibleSpeedMetersPerSecond + PositionJumpMarginMeters;
            if (Math.Abs(front - train.Front) > plausible)
            {
                return Fault(train, front < train.Front
                    ? $"its position went back from {train.Front:0.0} m to {front:0.0} m in {message.Sequence - train.LastSequence} tick(s) (a reset?)"
                    : $"its position jumped from {train.Front:0.0} m to {front:0.0} m in {message.Sequence - train.LastSequence} tick(s)");
            }
        }
        else
        {
            if (front > InitialReportToleranceMeters)
                return reject($"the initial position report is {front:0.0} m; a newly activated train starts at 0. Not placed.");
            if (StartBlockProblem(FindBlock(train.StartBlockId)!, trainId) is { } unsafeStart)
            {
                _trains.Remove(trainId);
                Raise(TrainTrackingEventKind.PlacementWithdrawn, train, unsafeStart);
                return new(TrainPositionOutcome.PlacementFailed, $"Train {trainId} was not placed: {unsafeStart}", trainId, train.DispatchRequestId);
            }
        }

        var plan = PlanPath(train, front);
        if (plan.Error is { } error)
        {
            return train.HasReport ? Fault(train, error) : reject($"{error}. Not placed.");
        }

        var steps = PlanSteps(train, plan.Path, plan.Starts, front, length);
        if (ValidateSteps(train, plan.Path, steps) is { } conflict)
        {
            return train.HasReport ? Fault(train, conflict) : reject($"{conflict}. Not placed.");
        }

        bool placing = !train.HasReport;
        train.Path = plan.Path;
        train.PathStart = plan.Starts;
        train.Front = front;
        train.Length = length;
        train.Speed = speed;
        train.LastSequence = message.Sequence;
        train.HasReport = true;
        train.Status = TrainTrackingStatus.Active;
        foreach (var (first, head) in steps)
        {
            ApplyFootprint(train, first, head);
        }

        // Blocks ahead of the front (left by backing up) are no longer part of the train's path; the path
        // is re-determined from the track if the front advances again. Traversed boundaries never change.
        if (train.HeadIndex < train.Path.Count - 1)
        {
            train.Path.RemoveRange(train.HeadIndex + 1, train.Path.Count - train.HeadIndex - 1);
            train.PathStart.RemoveRange(train.HeadIndex + 1, train.PathStart.Count - train.HeadIndex - 1);
        }

        foreach (var id in train.Footprint)
        {
            FindBlock(id)!.ActualSpeedMetersPerSecond = Math.Abs(speed);
        }

        if (placing)
        {
            Raise(TrainTrackingEventKind.Placed, train, $"initial position report received; train {trainId} occupies its start block {train.StartBlockId}");
        }

        if (plan.Fault is { } fault)
        {
            return Fault(train, fault);
        }

        // Every tick reports a position; listeners are told only when occupancy actually changed, so a
        // 10 Hz report stream does not republish the whole track each tick.
        if (steps.Count > 0)
        {
            Changed();
        }

        return placing
            ? new(TrainPositionOutcome.Placed, $"Train {trainId} placed at {train.StartBlockId}.", trainId, train.DispatchRequestId)
            : new(TrainPositionOutcome.Applied, $"Train {trainId}: front {front:0.0} m in {train.HeadBlockId}.", trainId, train.DispatchRequestId);
    }

    /// <summary>
    /// Replaces the route ahead of a placed train's front. Everything already traversed is kept; the new
    /// route must start in the train's traversed path (CTC may not have seen its latest block yet), match
    /// what it traversed since, and continue over valid connections and switch branches.
    /// </summary>
    public TrainPlacementResult UpdateTrainRoute(TrainRouteUpdateMessage update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (string.IsNullOrWhiteSpace(update.RequestId) || update.RequestId.Length > 128)
            return Refuse("A route update needs a request ID of up to 128 characters.");
        if (!TrainIdConvention.TryNormalize(update.TrainId, out var trainId) || !_trains.TryGetValue(trainId, out var train))
            return Refuse($"Train {update.TrainId} is not tracked by the Track Model.");
        if (train.Status != TrainTrackingStatus.Active)
            return Refuse(train.Status == TrainTrackingStatus.Faulted
                ? $"Train {trainId} is faulted ({train.Fault}); its route cannot be changed."
                : $"Train {trainId} is not on the track yet; send a new dispatch instead.");
        if (update.LineId != train.LineId)
            return Refuse($"Train {trainId} runs on the {train.LineId} Line, not the {update.LineId} Line.");
        var route = update.RouteBlockIds ?? [];
        if (route.Count == 0)
            return Refuse("The route has no blocks.");

        int head = train.HeadIndex;
        int from = -1;
        for (int j = head; j >= 0 && from < 0; j--)
        {
            if (train.Path[j] == route[0] && route.Count > head - j && train.Path.Skip(j).Take(head - j + 1).SequenceEqual(route.Take(head - j + 1)))
                from = j;
        }

        if (from < 0)
            return Refuse($"The new route for train {trainId} starts at {route[0]}, which does not lead through its front's block {train.HeadBlockId} along the path it traversed.");

        var ahead = route.Skip(head - from + 1).ToList();
        var check = new List<string> { train.Path[head] }.Concat(ahead).ToList();
        if (TrackTopology.ValidateRoute(FindBlock, train.LineId, check, head > 0 ? train.Path[head - 1] : null) is { } invalid)
            return Refuse(invalid);

        train.PlannedRoute = [.. train.Path.Take(head + 1), .. ahead];
        return new(TrainPlacementOutcome.Registered, $"Route of train {trainId} updated: {ahead.Count} block(s) ahead of {train.HeadBlockId}.");
    }

    /// <summary>
    /// Takes a train off the track: clears exactly the blocks it occupies (never another train's) and
    /// forgets its tracking. Also removes a train placed by block (<see cref="ApplyTrainUpdate"/>). True when a train was removed.
    /// </summary>
    public bool RetireTrain(string trainId, string reason)
    {
        if (!TrainIdConvention.TryNormalize(trainId, out var id)) id = trainId?.Trim() ?? string.Empty;
        if (_trains.Remove(id, out var train))
        {
            var cleared = ClearFootprint(train);
            if (cleared.Count > 0) OccupancyTransition?.Invoke(this, new(train.TrainId, train.LineId, [], cleared, string.Empty));
            Raise(train.Status == TrainTrackingStatus.PendingPlacement ? TrainTrackingEventKind.PlacementWithdrawn : TrainTrackingEventKind.Retired, train, reason);
            Changed();
            return true;
        }

        var legacy = Layout.Blocks.Where(b => b.TrainId == id).ToList();
        if (legacy.Count == 0) return false;
        foreach (var b in legacy) ClearBlock(b);
        OccupancyTransition?.Invoke(this, new(id, legacy[0].LineId, [], legacy.Select(b => b.Id).ToList(), string.Empty));
        Raise(TrainTrackingEventKind.Retired, id, legacy[0].LineId, string.Empty, string.Empty, reason);
        Changed();
        return true;
    }

    /// <summary>Why <paramref name="start"/> cannot receive a new train now; null when it can.</summary>
    private string? StartBlockProblem(TrackBlock start, string trainId)
    {
        if (start.IsOccupied)
            return $"start block {start.Id} is occupied{(start.TrainId.Length > 0 ? $" by train {start.TrainId}" : string.Empty)}";
        if (start.IsClosed)
            return $"start block {start.Id} is closed for maintenance";
        if (start.HasFailure)
            return $"start block {start.Id} has a failure (broken rail, track circuit or power)";
        var reserved = _trains.Values.FirstOrDefault(t => t.TrainId != trainId && t.Status == TrainTrackingStatus.PendingPlacement && t.StartBlockId == start.Id);
        return reserved is null ? null : $"start block {start.Id} is reserved for the pending placement of train {reserved.TrainId}";
    }

    private sealed record PathPlan(List<string> Path, List<double> Starts, string? Fault, string? Error);

    /// <summary>
    /// The traversed path extended until it contains <paramref name="front"/>, following the physical track
    /// and checked against the route. Nothing is changed. <c>Error</c>: the report cannot be applied at all;
    /// <c>Fault</c>: it can be applied, but the train went where its route does not.
    /// </summary>
    private PathPlan PlanPath(TrainTrack train, double front)
    {
        var path = train.Path.ToList();
        var starts = train.PathStart.ToList();
        string? fault = null;
        while (true)
        {
            int last = path.Count - 1;
            var lastBlock = FindBlock(path[last])!;
            double end = starts[last] + lastBlock.LengthMeters;
            if (front < end)
                return new(path, starts, fault, null);

            if (fault is not null)
                return new(path, starts, null, $"{fault}; the train cannot be tracked beyond {lastBlock.Id}");

            bool onRoute = path.Count <= train.PlannedRoute.Count && path.SequenceEqual(train.PlannedRoute.Take(path.Count));
            string? planned = onRoute && path.Count < train.PlannedRoute.Count ? train.PlannedRoute[path.Count] : null;
            if (planned is null)
            {
                if (front - end <= RouteEndToleranceMeters)
                    return new(path, starts, null, null); // stopped just past its route's end: still in the last route block
                return new(path, starts, null, $"its front ({front:0.0} m) is beyond the end of its route ({lastBlock.Id} ends at {end:0.0} m)");
            }

            var (next, problem) = TrackTopology.PhysicalNext(lastBlock, last > 0 ? path[last - 1] : null, planned);
            if (next is null)
                return new(path, starts, null, problem ?? $"no block follows {lastBlock.Id}");
            var nextBlock = FindBlock(next)!;
            if (nextBlock.LineId != train.LineId)
                return new(path, starts, null, $"block {next} is not on the {train.LineId} Line");
            if (nextBlock.IsOccupied)
                return new(path, starts, null, nextBlock.TrainId == train.TrainId
                    ? $"its front would re-enter block {next}, which the train still occupies"
                    : $"its front would enter block {next}, occupied by {(nextBlock.TrainId.Length > 0 ? "train " + nextBlock.TrainId : "another train")}");
            if (nextBlock.IsClosed)
                return new(path, starts, null, $"its front would enter block {next}, which is closed for maintenance");

            if (TrackTopology.BoundaryAlignmentProblem(lastBlock, nextBlock) is { } misaligned)
                fault = $"it passed {lastBlock.Id} -> {next} although {misaligned} (switch run through)";
            else if (next != planned)
                fault = $"switch {lastBlock.Id} is {lastBlock.Switch} and sends the train to {next}, but its route continues to {planned}";

            path.Add(next);
            starts.Add(end);
        }
    }

    /// <summary>
    /// The footprints (first, head path indices) the train passes through between its current front and
    /// <paramref name="front"/>, one per boundary event in travel order, ending with the final footprint.
    /// </summary>
    private List<(int First, int Head)> PlanSteps(TrainTrack train, List<string> path, List<double> starts, double front, double length)
    {
        var steps = new List<(int First, int Head)>();
        double from = train.HasReport ? train.Front : 0;
        if (!train.HasReport)
            steps.Add(FootprintAt(path, starts, 0, length, leftLimit: false));

        var critical = new List<double>();
        for (int i = 0; i < path.Count; i++)
        {
            double end = starts[i] + FindBlock(path[i])!.LengthMeters;
            if (i > 0) critical.Add(starts[i]);
            critical.Add(end + length);
        }

        bool forward = front >= from;
        var crossed = forward
            ? critical.Where(c => c > from + Epsilon && c <= front + Epsilon).OrderBy(c => c)
            : critical.Where(c => c > front + Epsilon && c <= from + Epsilon).OrderByDescending(c => c);
        double? previous = null;
        foreach (double c in crossed)
        {
            if (previous is { } p && Math.Abs(p - c) < Epsilon) continue;
            previous = c;
            steps.Add(FootprintAt(path, starts, c, length, leftLimit: !forward));
        }

        steps.Add(FootprintAt(path, starts, front, length, leftLimit: false));

        var current = train.HasReport ? (train.FirstIndex, train.HeadIndex) : (-1, -1);
        var distinct = new List<(int, int)>();
        foreach (var step in steps)
        {
            if (step != current && (distinct.Count == 0 || distinct[^1] != step)) distinct.Add(step);
            current = step;
        }

        return distinct;
    }

    /// <summary>
    /// The footprint with the front at <paramref name="front"/>: the head is the last path block whose start
    /// is at or before the front, the first occupied block the first one whose end is beyond the rear. With
    /// <paramref name="leftLimit"/> the boundaries are taken as approached from above (moving backwards).
    /// </summary>
    private (int First, int Head) FootprintAt(List<string> path, List<double> starts, double front, double length, bool leftLimit)
    {
        int head = 0;
        for (int i = 1; i < path.Count; i++)
        {
            if (leftLimit ? starts[i] < front - Epsilon : starts[i] <= front + Epsilon) head = i;
        }

        double rear = front - length;
        int first = head;
        for (int i = 0; i <= head; i++)
        {
            double end = starts[i] + FindBlock(path[i])!.LengthMeters;
            if (leftLimit ? end >= rear - Epsilon : end > rear + Epsilon)
            {
                first = i;
                break;
            }
        }

        return (first, head);
    }

    /// <summary>A reason the steps cannot be applied (a block the footprint grows into is another train's or closed, or a switch behind the train was moved); null when they can.</summary>
    private string? ValidateSteps(TrainTrack train, List<string> path, List<(int First, int Head)> steps)
    {
        var occupied = new HashSet<string>(train.Footprint, StringComparer.Ordinal);
        foreach (var (first, head) in steps)
        {
            for (int j = first; j <= head; j++)
            {
                var block = FindBlock(path[j])!;
                if (occupied.Contains(block.Id)) continue;
                if (block.IsOccupied && block.TrainId != train.TrainId)
                    return $"its footprint would extend into block {block.Id}, occupied by {(block.TrainId.Length > 0 ? "train " + block.TrainId : "another train")}";
                if (block.IsClosed)
                    return $"its footprint would extend into block {block.Id}, which is closed for maintenance";
                // The rear moving back over a boundary passes the points again: they must still be set for the path.
                if (j < head && TrackTopology.BoundaryAlignmentProblem(block, FindBlock(path[j + 1])!) is { } misaligned && train.Footprint.Count > 0 && j < train.FirstIndex)
                    return $"its rear would move back into {block.Id} although {misaligned}";
            }

            occupied = [.. path.Skip(first).Take(head - first + 1)];
        }

        return null;
    }

    /// <summary>Applies one footprint step to the blocks and reports it.</summary>
    private void ApplyFootprint(TrainTrack train, int first, int head)
    {
        var ids = train.Path.Skip(first).Take(head - first + 1).ToList();
        var next = new HashSet<string>(ids, StringComparer.Ordinal);
        var cleared = train.Footprint.Where(id => !next.Contains(id)).ToList();
        var entered = ids.Where(id => !train.Footprint.Contains(id)).Distinct().ToList();
        string headId = train.Path[head];
        foreach (var id in cleared)
        {
            var block = FindBlock(id)!;
            if (block.TrainId == train.TrainId) ClearBlock(block);
        }

        for (int j = first; j <= head; j++)
        {
            var block = FindBlock(train.Path[j])!;
            if (!train.Footprint.Contains(block.Id))
            {
                block.IsOccupied = true;
                block.TrainId = train.TrainId;
                // The physical boundary the train crossed into this block; a continuation code applies only across it.
                block.TrainEntryBlockId = j > 0 ? train.Path[j - 1] : string.Empty;
            }

            block.IsTrainHead = block.Id == headId;
        }

        string previousHead = train.HeadBlockId;
        train.Footprint = next;
        train.FirstIndex = first;
        train.HeadIndex = head;
        if (cleared.Count > 0 || entered.Count > 0 || previousHead != headId)
        {
            OccupancyTransition?.Invoke(this, new(train.TrainId, train.LineId, entered, cleared, headId));
        }
    }

    private List<string> ClearFootprint(TrainTrack train)
    {
        var cleared = new List<string>();
        foreach (var id in train.Footprint)
        {
            var block = FindBlock(id);
            if (block is not null && block.TrainId == train.TrainId)
            {
                ClearBlock(block);
                cleared.Add(id);
            }
        }

        train.Footprint = new HashSet<string>(StringComparer.Ordinal);
        return cleared;
    }

    private static void ClearBlock(TrackBlock block)
    {
        block.TrainId = string.Empty;
        block.IsOccupied = false;
        block.IsTrainHead = false;
        block.ActualSpeedMetersPerSecond = 0;
        block.TrainEntryBlockId = string.Empty;
    }

    private TrainPositionResult Fault(TrainTrack train, string reason)
    {
        train.Status = TrainTrackingStatus.Faulted;
        train.Fault = reason;
        Raise(TrainTrackingEventKind.Faulted, train, $"train {train.TrainId}: {reason}; its occupancy is held until it is retired");
        Changed();
        return new(TrainPositionOutcome.Faulted, $"Train {train.TrainId} faulted: {reason}. Its occupancy is held.", train.TrainId, train.DispatchRequestId);
    }

    private void Raise(TrainTrackingEventKind kind, TrainTrack train, string detail) =>
        Raise(kind, train.TrainId, train.LineId, train.DispatchRequestId, train.StartBlockId, detail);

    private void Raise(TrainTrackingEventKind kind, string trainId, string lineId, string requestId, string startBlockId, string detail) =>
        TrainTrackingChanged?.Invoke(this, new(kind, trainId, lineId, requestId, startBlockId, detail));

    /// <summary>Forgets every tracked train (layout replaced): their blocks no longer exist as they were.</summary>
    private void ForgetTrackedTrains(string reason)
    {
        foreach (var train in _trains.Values.ToList())
        {
            Raise(train.Status == TrainTrackingStatus.PendingPlacement ? TrainTrackingEventKind.PlacementWithdrawn : TrainTrackingEventKind.Retired, train, reason);
        }

        _trains.Clear();
    }

    private static TrainPlacementResult Refuse(string detail, bool retryable = false) => new(TrainPlacementOutcome.Rejected, detail, retryable);

    private static TrainTrackingSnapshot Snapshot(TrainTrack t) => new(
        t.TrainId, t.LineId, t.DispatchRequestId, t.StartBlockId, [.. t.PlannedRoute], [.. t.Path], t.Front, t.Length, t.Speed,
        t.HasReport ? t.HeadBlockId : string.Empty,
        t.HasReport ? [.. t.Path.Skip(t.FirstIndex).Take(t.HeadIndex - t.FirstIndex + 1)] : [],
        t.LastSequence, t.Status, t.Fault);

    /// <summary>Per-train tracking state. Mutated only by <see cref="TrackService"/>, on the host's single thread.</summary>
    private sealed class TrainTrack
    {
        public required string TrainId { get; init; }
        public required string LineId { get; init; }
        public required string DispatchRequestId { get; init; }
        public required string StartBlockId { get; init; }

        /// <summary>The route from the start block: the placement route, with any accepted route update.</summary>
        public List<string> PlannedRoute { get; set; } = [];

        /// <summary>Blocks the front physically entered, in order; <c>Path[0]</c> is the start block.</summary>
        public List<string> Path { get; set; } = [];

        /// <summary>Cumulative position of each path block's entry: <c>PathStart[0] = 0</c>.</summary>
        public List<double> PathStart { get; set; } = [];

        public double Front { get; set; }
        public double Length { get; set; }
        public double Speed { get; set; }
        public long LastSequence { get; set; } = -1;
        public bool HasReport { get; set; }
        public TrainTrackingStatus Status { get; set; } = TrainTrackingStatus.PendingPlacement;
        public string Fault { get; set; } = string.Empty;
        public HashSet<string> Footprint { get; set; } = new(StringComparer.Ordinal);
        public int FirstIndex { get; set; }
        public int HeadIndex { get; set; }
        public string HeadBlockId => Footprint.Count > 0 ? Path[HeadIndex] : string.Empty;
    }
}
