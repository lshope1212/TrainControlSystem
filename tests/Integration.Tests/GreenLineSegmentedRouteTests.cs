using CTC.Core.Interfaces;
using CTC.Core.Models;
using CTC.Core.Routing;
using CTC.Core.Scheduling;
using CTC.Core.Services;
using TrackController.Contracts.Messages;
using TrackController.Core.Services;
using TrackController.Runtime;
using TrackModel.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Integration.Tests;

/// <summary>
/// The yard-to-Edgebrook Green Line journey through the production CTC and the production Track Controller
/// (course deployment, bundled PLC), in process and on a manual clock, so every movement request, handoff,
/// segment and switch command can be checked step by step. The Track Model is a stand-in that throws a switch
/// only when its block is clear, as the real one does; the train's footprint advances one block boundary at a
/// time, and only into a block a live Track Controller grant covers. Physics is not modelled here
/// (the full physical integration test runs the real modules); this proves the segmentation logic.
/// </summary>
[TestClass]
public sealed class GreenLineSegmentedRouteTests
{
    private static readonly string[] Route = CourseRoutes.YardToEdgebrook;

    /// <summary>Test C and D: bounded, accepted requests; automatic segments; switches set each way, each confirmed before use.</summary>
    [TestMethod]
    public async Task YardToEdgebrook_RunsOnBoundedRequests_AndSetsEachSwitchForEachPassage()
    {
        var loop = await WaysideLoop.StartAsync();
        await loop.RunUntilAsync(() => loop.Head == Route.Length - 1, maxCycles: 3000);

        Console.WriteLine(string.Join(Environment.NewLine, loop.Segments));
        Assert.IsEmpty(loop.Violations, string.Join(Environment.NewLine, loop.Violations));
        Assert.IsEmpty(loop.FirstSubmissionRejections, string.Join(Environment.NewLine, loop.FirstSubmissionRejections));

        // CTC tracked every route occurrence and saw the arrival.
        var train = (await loop.Train())!;
        Assert.AreEqual(116, train.ActiveRouteIndex);
        Assert.AreEqual("GREEN-9", train.CurrentBlockId);

        // Requests: never a repeated block, and the journey was covered by explicit segments.
        foreach (var request in loop.Submitted.Where(message => message.IsActive))
        {
            var path = new[] { request.TargetBlockId }.Concat(request.PathBlockIds).ToList();
            Assert.HasCount(path.Count, path.Distinct().ToList(), $"Request {request.RequestId} at {request.TargetBlockId} revisits no block.");
        }

        var ordinary = loop.Submitted.Where(message => message.IsActive && message.PredecessorRequestId is null).GroupBy(message => message.RequestId).Select(group => group.First()).ToList();
        Assert.IsTrue(ordinary.Any(message => message.PathBlockIds.LastOrDefault() == "GREEN-99"), "A first segment ends at GREEN-99.");
        var extension = ordinary.First(message => message.PathBlockIds.Contains("GREEN-100"));
        Assert.AreEqual("GREEN-9", extension.PathBlockIds[^1], "The next segment runs to Edgebrook.");
        int extensionTarget = Array.IndexOf(Route, extension.TargetBlockId, 24);
        Assert.IsTrue(extensionTarget is >= 24 and <= 36, $"Requested from the loop after switch 85's zone (from {extension.TargetBlockId}).");
        Assert.IsTrue(loop.Submitted.Any(message => message.PredecessorRequestId is not null), "Within a segment, authority is handed over block to block.");

        // Every switch: commanded by the Track Controller and thrown by the Track Model once per change, in route order.
        CollectionAssert.AreEqual(new[] { SwitchPosition.Normal, SwitchPosition.Reverse }, loop.Commanded("GREEN-77"));
        CollectionAssert.AreEqual(new[] { SwitchPosition.Normal, SwitchPosition.Reverse }, loop.Physical("GREEN-77"));
        CollectionAssert.AreEqual(new[] { SwitchPosition.Normal, SwitchPosition.Reverse }, loop.Commanded("GREEN-85"));
        CollectionAssert.AreEqual(new[] { SwitchPosition.Normal, SwitchPosition.Reverse }, loop.Physical("GREEN-85"));
        CollectionAssert.AreEqual(new[] { SwitchPosition.Normal, SwitchPosition.Reverse }, loop.Physical("GREEN-28"));
        CollectionAssert.AreEqual(new[] { SwitchPosition.Normal }, loop.Physical("GREEN-13"));
        CollectionAssert.AreEqual(
            new[] { "GREEN-77@14:Normal", "GREEN-85@22:Normal", "GREEN-85@38:Reverse", "GREEN-77@46:Reverse", "GREEN-28@97:Reverse", "GREEN-13@112:Normal" },
            loop.SwitchCrossings.ToArray(),
            "Each passage found its switch reported (and commanded) in the position it needs.");
    }

    /// <summary>The reason segments exist: the Track Controller refuses one request with both passages of N.</summary>
    [TestMethod]
    public async Task OneRequestForTheWholeJourney_IsRefusedByTheTrackController()
    {
        var loop = await WaysideLoop.StartAsync(placeTrain: false);
        loop.SetFootprint(0, 0);
        loop.Cycle();
        var whole = new MovementSuggestionMessage
        {
            RequestId = "whole-journey",
            TrainId = "001",
            TargetBlockId = Route[0],
            PathBlockIds = [.. Route.Skip(1)],
            SuggestedSpeedMetersPerSecond = 5,
            SuggestedAuthorityMeters = 1000,
        };
        var refused = Assert.ThrowsExactly<ArgumentException>(() => loop.Wayside.SubmitMovement(whole));
        StringAssert.Contains(refused.Message, "revisit");
    }

    /// <summary>
    /// Test E: something occupies GREEN-84 (switch 85's protection zone) while the train runs round the loop.
    /// CTC holds the train in its first segment, the Track Controller never moves 85 or grants into GREEN-100,
    /// and the train stops at the segment end (GREEN-99), outside the zone. When GREEN-84 clears, the next
    /// segment is requested, 85 is set Reverse and confirmed, and the train continues to Edgebrook.
    /// </summary>
    [TestMethod]
    public async Task BlockedZoneAtTheSegmentBoundary_HoldsTheTrainOutsideIt_AndItRecovers()
    {
        var loop = await WaysideLoop.StartAsync();
        await loop.RunUntilAsync(() => loop.Head == 23, maxCycles: 600);
        loop.Obstacles.Add("GREEN-84");
        await loop.RunUntilAsync(() => loop.Head == 36 && loop.Rear == 36, maxCycles: 600);

        // Held: many evaluations later, still at GREEN-99, with 85 untouched and nothing granted into GREEN-100.
        for (int i = 0; i < 60; i++)
        {
            await loop.CycleAsync();
            Assert.AreEqual(36, loop.Head, "Never enters GREEN-100 while 85's zone is blocked.");
            Assert.AreEqual(SwitchPosition.Normal, loop.Wayside.GetSnapshot().Blocks.Single(block => block.BlockId == "GREEN-85").CommandedSwitch);
            Assert.IsFalse(loop.GrantCovers("GREEN-100"), "No grant into the junction.");
        }

        StringAssert.Contains((await loop.Train())!.NextSegmentWaitReason, "GREEN-84");
        Assert.IsFalse(loop.Submitted.Any(message => message.IsActive && message.PathBlockIds.Contains("GREEN-100")), "CTC never even asked.");

        loop.Obstacles.Clear();
        await loop.RunUntilAsync(() => loop.Head == Route.Length - 1, maxCycles: 3000);
        Assert.IsEmpty(loop.Violations, string.Join(Environment.NewLine, loop.Violations));
        CollectionAssert.AreEqual(new[] { SwitchPosition.Normal, SwitchPosition.Reverse }, loop.Physical("GREEN-85"));
    }

    /// <summary>
    /// Test E: the switch machines do not respond when the next segment is requested. The Track Controller commands
    /// 85 and 77 Reverse but withholds all authority on the unconfirmed alignment, so the train never reaches the
    /// junction; once the Track Model reports the throw, the grant follows and the train continues.
    /// </summary>
    [TestMethod]
    public async Task UnconfirmedSwitchThrow_WithholdsAuthority_UntilTheTrackModelReportsIt()
    {
        var loop = await WaysideLoop.StartAsync();
        await loop.RunUntilAsync(() => loop.Head == 23, maxCycles: 600);
        loop.SwitchesRespond = false;
        await loop.RunUntilAsync(() => loop.Submitted.Any(message => message.IsActive && message.PathBlockIds.Contains("GREEN-100")), maxCycles: 600);

        for (int i = 0; i < 60; i++)
        {
            await loop.CycleAsync();
            Assert.IsLessThan(37, loop.Head, "Never into GREEN-100 on an unconfirmed throw.");
        }

        var snapshot = loop.Wayside.GetSnapshot().Blocks.ToDictionary(block => block.BlockId);
        Assert.AreEqual(SwitchPosition.Reverse, snapshot["GREEN-85"].CommandedSwitch, "Commanded for the next segment...");
        Assert.AreEqual(SwitchPosition.Normal, snapshot["GREEN-85"].ReportedSwitch, "...but not reported.");
        var held = snapshot[Route[loop.Head]];
        Assert.AreEqual(0d, held.AuthorizedAuthorityMeters, held.MovementReason);
        StringAssert.Contains(held.MovementReason, "switch");

        loop.SwitchesRespond = true;
        await loop.RunUntilAsync(() => loop.Head == Route.Length - 1, maxCycles: 3000);
        Assert.IsEmpty(loop.Violations, string.Join(Environment.NewLine, loop.Violations));
    }

    /// <summary>
    /// CTC and the Track Controller joined directly: CTC's sends are submitted to the controller and acknowledged as
    /// the wayside host does, and the controller's snapshot is reported back to CTC as block statuses.
    /// </summary>
    private sealed class WaysideLoop : IMessageSender
    {
        private readonly Dictionary<string, TrackModelBlockStateMessage> _track;
        private readonly Dictionary<string, double> _lengths;
        private readonly Queue<object> _outbox = new();
        private readonly Dictionary<string, List<SwitchPosition>> _commanded = [], _physical = [];
        private readonly HashSet<string> _seenRequestIds = [];
        private readonly LineTopology _topology;
        private TimeSpan _systemTime = new(14, 0, 0);

        private WaysideLoop()
        {
            var configuration = WaysideDeployment.Read(LiveSystem.CourseDeployment).Territories.Single(territory => territory.Id == "wayside-green").Configuration;
            Wayside = new TrackControllerService(configuration, Clock);
            Wayside.LoadPlc("wayside.plc", File.ReadAllText(Path.Combine(LiveSystem.SamplesFolder, "wayside.plc")));
            _lengths = configuration.Blocks.ToDictionary(block => block.BlockId, block => block.LengthMeters);
            _track = configuration.Blocks.ToDictionary(block => block.BlockId, block => new TrackModelBlockStateMessage
            {
                BlockId = block.BlockId,
                Occupancy = OccupancyState.Clear,
                Signal = block.HasSignal ? SignalState.Red : SignalState.Unknown,
                Switch = block.NormalNextBlockId is not null ? SwitchPosition.Normal : SwitchPosition.Unknown,
                Crossing = block.HasCrossing ? CrossingState.Open : CrossingState.Unknown,
            });
            foreach (var id in configuration.Blocks.Where(block => block.NormalNextBlockId is not null).Select(block => block.BlockId))
            {
                _commanded[id] = [];
                _physical[id] = [];
            }

            Ctc = new CTCService(this, Clock);
            var model = new TrackService();
            CourseTrackLayout.Load(model);
            Ctc.ApplyTrackLayout(model.CreateLayoutMessage());
            _topology = LineTopology.From(Ctc.State.FindLine("Green")!);
        }

        public ManualClock Clock { get; } = new();

        public TrackControllerService Wayside { get; }

        public CTCService Ctc { get; }

        /// <summary>Route index of the train's front and rear; -1 before it is placed.</summary>
        public int Head { get; private set; } = -1;

        public int Rear { get; private set; } = -1;

        /// <summary>Blocks occupied by something other than the train.</summary>
        public HashSet<string> Obstacles { get; } = [];

        /// <summary>False: the switch machines do not respond to commands.</summary>
        public bool SwitchesRespond { get; set; } = true;

        /// <summary>Every movement message CTC sent, in order.</summary>
        public List<MovementSuggestionMessage> Submitted { get; } = [];

        /// <summary>Requests the controller refused the first time CTC sent them.</summary>
        public List<string> FirstSubmissionRejections { get; } = [];

        /// <summary>Anything that would be unsafe: entering an ungranted block, passing a misaligned switch, conflicting claims.</summary>
        public List<string> Violations { get; } = [];

        /// <summary>Each switch passage as "switch@routeIndex:position the Track Model had".</summary>
        public List<string> SwitchCrossings { get; } = [];

        /// <summary>Each ordinary request's span, for the test log.</summary>
        public List<string> Segments { get; } = [];

        public static async Task<WaysideLoop> StartAsync(bool placeTrain = true)
        {
            var loop = new WaysideLoop();
            loop.Cycle();
            loop.Wayside.SetSimulation(false, 1);
            loop.Cycle();
            await loop.SynchronizeCtcAsync();

            var template = ScheduleTemplateFactory.Create(loop.Ctc.State.FindLine("Green")!, TrainIds.Allocate(loop.Ctc.State, 1));
            template.Rows.Single(row => row.BlockId == "GREEN-63").SetTrainTime(0, "14:00:00");
            template.Rows.Single(row => row.BlockId == "GREEN-9").SetTrainTime(0, "15:00:00");
            var schedule = ScheduleTemplateConverter.Convert(template);
            Assert.IsTrue(schedule.IsSuccess, schedule.ErrorMessage);
            CollectionAssert.AreEqual(Route, schedule.Trains.Single().Route.Select(block => block.BlockId).ToArray());
            loop.Ctc.QueueSchedule(schedule.Trains);
            if (placeTrain)
            {
                await loop.Ctc.SetSystemTimeAsync(loop._systemTime);
                loop.DeliverOutbox();
                Assert.IsNotNull(await loop.Train(), "CTC released Train 001.");
                loop.SetFootprint(0, 0); // the Track Model places it in GREEN-63 on CTC's dispatch
                await loop.CycleAsync();
                Assert.AreEqual(TrainReleaseStatus.DetectedOnTrack, (await loop.Train())!.ReleaseStatus);
            }

            return loop;
        }

        public Task<DispatchedTrainState?> Train() => Task.FromResult(Ctc.State.FindDispatchedTrain("001"));

        public SwitchPosition[] Commanded(string id) => [.. _commanded[id]];

        public SwitchPosition[] Physical(string id) => [.. _physical[id]];

        public void SetFootprint(int rear, int head) => (Rear, Head) = (rear, head);

        public async Task RunUntilAsync(Func<bool> done, int maxCycles)
        {
            for (int i = 0; i < maxCycles && !done(); i++)
            {
                await CycleAsync();
            }

            Assert.IsTrue(done(), $"Not reached within {maxCycles} cycles; train at route index {Head} ({(Head >= 0 ? Route[Head] : "-")}). "
                + $"Wayside: {(Head >= 0 ? Wayside.GetSnapshot().Blocks.Single(block => block.BlockId == Route[Head]).MovementReason : "")} "
                + $"CTC: {(await Train())?.NextSegmentWaitReason}");
        }

        /// <summary>One Track Model cycle (commands applied, train moved, feedback reported), then CTC's reaction.</summary>
        public async Task CycleAsync()
        {
            Cycle();
            await SynchronizeCtcAsync();
            var train = await Train();
            if (train is not null && Head >= 0 && train.CurrentBlockOccupancyObserved && train.ActiveRouteIndex != Head)
            {
                Violations.Add($"CTC places the train at route index {train.ActiveRouteIndex} ({train.CurrentBlockId}), but its front is at {Head} ({Route[Head]}).");
            }
        }

        /// <summary>The Track Model side of one cycle: apply the controller's commands, move the train, report every block.</summary>
        public void Cycle()
        {
            var snapshot = Wayside.GetSnapshot();
            foreach (var block in snapshot.Blocks)
            {
                var track = _track[block.BlockId];
                if (block.HasSwitch && SwitchesRespond && track.Occupancy == OccupancyState.Clear && block.CommandedSwitch is SwitchPosition.Normal or SwitchPosition.Reverse)
                {
                    _track[block.BlockId] = track = track with { Switch = block.CommandedSwitch };
                }

                if (block.HasCrossing && block.CommandedCrossing is CrossingState.Open or CrossingState.Closed)
                {
                    _track[block.BlockId] = track with { Crossing = block.CommandedCrossing };
                }

                if (block.HasSwitch)
                {
                    Record(_commanded[block.BlockId], block.CommandedSwitch);
                }

                if (block.SwitchAlignment.Contains("claimed by", StringComparison.Ordinal))
                {
                    Violations.Add($"Conflicting switch claims: {block.SwitchAlignment}");
                }
            }

            MoveTrain(snapshot);
            foreach (var (id, track) in _track.ToList())
            {
                bool occupied = Obstacles.Contains(id) || (Head >= 0 && (Route[Head] == id || Route[Rear] == id));
                _track[id] = track with { Occupancy = occupied ? OccupancyState.Occupied : OccupancyState.Clear };
                if (_physical.TryGetValue(id, out var history))
                {
                    Record(history, _track[id].Switch);
                }
            }

            Clock.Advance(0.25);
            Wayside.ApplyFeedback(_track.Values.Select(row => row with { ObservedAtUtc = Clock.GetUtcNow() }).ToList());
        }

        /// <summary>Whether a positive grant in the controller's latest evaluation covers <paramref name="blockId"/> beyond its own block.</summary>
        public bool GrantCovers(string blockId) => GrantCovers(Wayside.GetSnapshot(), blockId);

        private bool GrantCovers(WaysideSnapshotMessage snapshot, string blockId) =>
            snapshot.Blocks.Any(block => block.Request is { } request && block.AuthorizedAuthorityMeters > 0 && block.AuthorizedSpeedMetersPerSecond > 0
                && Covered(request, block.AuthorizedAuthorityMeters).Skip(1).Contains(blockId));

        private IEnumerable<string> Covered(MovementSuggestionMessage request, double authority)
        {
            double remaining = authority;
            foreach (string id in new[] { request.TargetBlockId }.Concat(request.PathBlockIds))
            {
                if (remaining <= 1e-6)
                {
                    yield break;
                }

                yield return id;
                remaining -= _lengths[id];
            }
        }

        /// <summary>One boundary per cycle: the rear catches up, or the front enters the next block if a grant covers it.</summary>
        private void MoveTrain(WaysideSnapshotMessage snapshot)
        {
            if (Head < 0)
            {
                return;
            }

            if (Rear < Head)
            {
                Rear = Head;
                return;
            }

            if (Head + 1 >= Route.Length || !GrantCovers(snapshot, Route[Head + 1]) || _track[Route[Head + 1]].Occupancy != OccupancyState.Clear)
            {
                return;
            }

            // The points between the two blocks must be set for this crossing.
            foreach (var (switchId, other, index) in new[] { (Route[Head], Route[Head + 1], Head), (Route[Head + 1], Route[Head], Head + 1) })
            {
                if (_topology.LegPosition(switchId, other) is { } needed)
                {
                    var physical = _track[switchId].Switch;
                    var reported = snapshot.Blocks.Single(block => block.BlockId == switchId);
                    if (physical != needed || reported.CommandedSwitch != needed)
                    {
                        Violations.Add($"Crossed {Route[Head]} -> {Route[Head + 1]} with switch {switchId} {physical} (commanded {reported.CommandedSwitch}); it needs {needed}.");
                    }

                    // One entry per passage of the switch block: recorded where it is entered or left by a leg.
                    string entry = $"{switchId}@{index}:{physical}";
                    if (!SwitchCrossings.Contains(entry))
                    {
                        SwitchCrossings.Add(entry);
                    }
                }
            }

            Head++;
        }

        private async Task SynchronizeCtcAsync()
        {
            var snapshot = Wayside.GetSnapshot();
            foreach (var block in snapshot.Blocks)
            {
                bool granted = block.MovementStatus is "Approved" or "Restricted" && block.AuthorizedAuthorityMeters > 0 && block.AuthorizedSpeedMetersPerSecond > 0;
                Ctc.ApplyBlockStatus(new BlockStatusMessage
                {
                    BlockId = block.BlockId,
                    Occupancy = block.IsFresh && !block.IsFaulted ? block.Occupancy : OccupancyState.Unknown,
                    Signal = block.IsFresh ? block.ReportedSignal : SignalState.Unknown,
                    Switch = block.IsFresh ? block.ReportedSwitch : SwitchPosition.Unknown,
                    Crossing = block.IsFresh ? block.ReportedCrossing : CrossingState.Unknown,
                    Maintenance = block.Maintenance == "Closed" ? MaintenanceState.Closed : MaintenanceState.Open,
                    ReportedAtUtc = snapshot.EvaluatedAtUtc,
                    Movement = block.Request is { } request
                        ? new MovementAuthorizationReport
                        {
                            RequestId = request.RequestId,
                            TrainId = request.TrainId,
                            State = granted ? (block.MovementStatus == "Approved" ? MovementAuthorizationState.Approved : MovementAuthorizationState.Restricted) : MovementAuthorizationState.Withheld,
                            AuthorizedSpeedMetersPerSecond = granted ? block.AuthorizedSpeedMetersPerSecond : 0,
                            AuthorizedAuthorityMeters = granted ? block.AuthorizedAuthorityMeters : 0,
                            Reason = block.MovementReason,
                            SupersededRequestId = request.PredecessorRequestId ?? string.Empty,
                        }
                        : null,
                });
            }

            _systemTime += TimeSpan.FromSeconds(1);
            await Ctc.SetSystemTimeAsync(_systemTime);
            await Ctc.SynchronizeMovementRequestsAsync();
            DeliverOutbox();
        }

        /// <summary>What the wayside host does with each CTC message: submit it and acknowledge it.</summary>
        private void DeliverOutbox()
        {
            while (_outbox.TryDequeue(out var message))
            {
                if (message is not MovementSuggestionMessage movement)
                {
                    continue; // physical dispatch: the test places the train itself
                }

                Submitted.Add(movement);
                bool first = movement.IsActive && _seenRequestIds.Add(movement.RequestId);
                if (first && movement.PredecessorRequestId is null)
                {
                    Segments.Add($"{movement.TargetBlockId} -> {movement.PathBlockIds.LastOrDefault()} ({movement.PathBlockIds.Count + 1} blocks, {movement.SuggestedAuthorityMeters:0} m)");
                }

                RequestAcknowledgementOutcome outcome;
                string detail;
                try
                {
                    var result = Wayside.SubmitMovement(movement);
                    outcome = result == MovementSubmission.Staged ? RequestAcknowledgementOutcome.PreparedAsSuccessor : RequestAcknowledgementOutcome.AcceptedForEvaluation;
                    detail = result.ToString();
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    outcome = RequestAcknowledgementOutcome.Rejected;
                    detail = ex.Message;
                    if (first)
                    {
                        FirstSubmissionRejections.Add($"{movement.RequestId} at {movement.TargetBlockId}: {ex.Message}");
                    }
                }

                if (movement.IsActive)
                {
                    Ctc.ApplyRequestAcknowledgement(new RequestAcknowledgementMessage
                    {
                        RequestId = movement.RequestId,
                        RequestType = nameof(MovementSuggestionMessage),
                        Outcome = outcome,
                        Detail = detail,
                        BlockId = movement.TargetBlockId,
                        TrainId = movement.TrainId,
                        AcknowledgedAtUtc = Clock.GetUtcNow(),
                    });
                }
            }
        }

        private static void Record(List<SwitchPosition> history, SwitchPosition value)
        {
            if (value is SwitchPosition.Normal or SwitchPosition.Reverse && (history.Count == 0 || history[^1] != value))
            {
                history.Add(value);
            }
        }

        Task IMessageSender.SendAsync<TMessage>(TMessage message, CancellationToken cancellationToken)
        {
            _outbox.Enqueue(message);
            return Task.CompletedTask;
        }
    }

    /// <summary>A wall clock the test advances by hand, shared by CTC and the Track Controller.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 12, 14, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(double seconds) => _now = _now.AddSeconds(seconds);
    }
}

/// <summary>Expected routes on the current course layout, shared by the Green Line route tests.</summary>
internal static class CourseRoutes
{
    /// <summary>GREEN-63 (yard) to GREEN-9 (Edgebrook): 117 block visits, N (77..85) twice.</summary>
    public static readonly string[] YardToEdgebrook =
    [
        .. Green(63, 77), .. Green(78, 85), .. Green(86, 100), .. Green(85, 77), .. Green(101, 150), .. Green(28, 13), .. Green(12, 9),
    ];

    private static IEnumerable<string> Green(int from, int to) =>
        (from <= to ? Enumerable.Range(from, to - from + 1) : Enumerable.Range(to, from - to + 1).Reverse()).Select(n => $"GREEN-{n}");
}
