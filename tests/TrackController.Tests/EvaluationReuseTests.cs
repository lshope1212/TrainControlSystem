using System.Text.Json;
using TrackController.Contracts.Messages;
using TrackController.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Tests;

/// <summary>
/// Snapshot requests reuse a settled evaluation only when evaluating again could not change anything: repeated
/// polling stops costing evaluations, while every input change, watchdog expiry, switch-throw window and approach
/// lock is still evaluated exactly as before (the differential test compares against a controller that evaluates
/// on every request).
/// </summary>
[TestClass]
public sealed class EvaluationReuseTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    private static string SamplePath(string name) => Path.Combine(AppContext.BaseDirectory, "PiSamples", name);

    private static readonly string Plc = File.ReadAllText(SamplePath("wayside.plc"));

    private static WaysideConfigurationMessage BlueLine() => JsonSerializer.Deserialize<WaysideConfigurationMessage>(
        File.ReadAllText(SamplePath("blue-line-track-map.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    /// <summary>The Blue Line in automatic operation with the bundled PLC (switch 5, crossing 3, signals 6 and 11).</summary>
    /// <param name="manual">Manual (non-automatic) running simulation instead, whose simulated time advances with the wall clock.</param>
    private static TrackControllerService Controller(Clock clock, bool reuse = true, bool manual = false)
    {
        var service = new TrackControllerService(BlueLine(), clock) { ReuseEvaluations = reuse };
        service.LoadPlc("wayside.plc", Plc);
        if (manual) service.SetSimulation(false, 3);
        else service.EnableAutomaticOperation();
        return service;
    }

    /// <summary>A Track Model stand-in: shows the commanded signals and crossings, throws commanded switches when clear.</summary>
    private sealed class Track(TrackControllerService service, Clock clock)
    {
        public readonly Dictionary<string, (OccupancyState Occupancy, SignalState Signal, SwitchPosition Switch, CrossingState Crossing)> State =
            service.GetConfiguration().Blocks.ToDictionary(block => block.BlockId, block => (OccupancyState.Clear, SignalState.Red, SwitchPosition.Normal, CrossingState.Open));

        public List<TrackModelBlockStateMessage> Observe() => State.Select(item => new TrackModelBlockStateMessage
        {
            BlockId = item.Key, ObservedAtUtc = clock.Now, Occupancy = item.Value.Occupancy, Signal = item.Value.Signal,
            Switch = item.Value.Switch, Crossing = item.Value.Crossing, IsReservedByCtc = false
        }).ToList();

        /// <summary>Applies the controller's commands to the field equipment, as the Track Model does.</summary>
        public void Follow(WaysideSnapshotMessage snapshot)
        {
            foreach (var block in snapshot.Blocks)
            {
                var state = State[block.BlockId];
                if (block.HasSignal) state.Signal = block.CommandedSignal;
                if (block.HasCrossing) state.Crossing = block.CommandedCrossing;
                if (block.HasSwitch && block.CommandedSwitch is SwitchPosition.Normal or SwitchPosition.Reverse && state.Occupancy == OccupancyState.Clear)
                    state.Switch = block.CommandedSwitch;
                State[block.BlockId] = state;
            }
        }

        public void Occupy(string id, bool occupied)
        {
            var state = State[id];
            state.Occupancy = occupied ? OccupancyState.Occupied : OccupancyState.Clear;
            State[id] = state;
        }
    }

    private static MovementSuggestionMessage Request(string id, string target, string[] path, bool active = true) => new()
    {
        RequestId = id, TrainId = "Blue 1", TargetBlockId = target, PathBlockIds = [.. path],
        SuggestedSpeedMetersPerSecond = 10, SuggestedAuthorityMeters = 50 * (path.Length + 1), IsActive = active
    };

    private static readonly string[] ToStationB = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "10"];

    private static readonly string[] ToStationC = ["1", "2", "3", "4", "5", "11", "12", "13", "14", "15"];

    /// <summary>A granted movement from block 1 toward Station B, with the Track Model following every command.</summary>
    private static (TrackControllerService Service, Track Track, Clock Clock) Granted()
    {
        var clock = new Clock();
        var service = Controller(clock);
        var track = new Track(service, clock);
        track.Occupy("1", true);
        service.ApplyFeedback(track.Observe());
        service.SubmitMovement(Request("r1", "1", ToStationB[1..]));
        for (int i = 0; i < 4; i++)
        {
            clock.Advance(0.1);
            track.Follow(service.GetSnapshot());
            service.ApplyFeedback(track.Observe());
        }

        Assert.IsGreaterThan(0, Authority(service.GetSnapshot()), Reason(service.GetSnapshot()));
        return (service, track, clock);
    }

    private static WaysideBlockStatus Origin(WaysideSnapshotMessage snapshot) => snapshot.Blocks.Single(block => block.BlockId == "1");

    private static double Authority(WaysideSnapshotMessage snapshot) => Origin(snapshot).AuthorizedAuthorityMeters;

    private static string Reason(WaysideSnapshotMessage snapshot) => Origin(snapshot).MovementReason;

    [TestMethod]
    public void RepeatedPolling_WithNothingChanged_DoesNotReevaluate()
    {
        var (service, _, clock) = Granted();
        service.GetSnapshot();
        service.GetSnapshot();
        long settled = service.EvaluationCount;
        var first = service.GetSnapshot();
        for (int i = 0; i < 50; i++)
        {
            clock.Advance(0.01);
            var snapshot = service.GetSnapshot();
            Assert.AreSame(first.Blocks, snapshot.Blocks, "The same evaluation is served.");
            Assert.AreEqual(clock.Now, snapshot.EvaluatedAtUtc, "Re-stamped for the moment it is served.");
            Assert.AreEqual(clock.Now.AddSeconds(1.5), snapshot.ValidUntilUtc, "Validity computed as a new evaluation's would be.");
        }

        Assert.AreEqual(settled, service.EvaluationCount, "Fifty polls in 0.5 s with no change cost no evaluation.");
    }

    [TestMethod]
    public void RepeatedPolling_StillEvaluatesAtLeastEverySecond()
    {
        var (service, _, clock) = Granted();
        service.GetSnapshot();
        service.GetSnapshot();
        long settled = service.EvaluationCount;
        clock.Advance(1.0);
        service.GetSnapshot();
        Assert.AreEqual(settled + 1, service.EvaluationCount, "Defense in depth: reuse never exceeds one second.");
    }

    [TestMethod]
    public void InputChange_IsEvaluatedAtOnce()
    {
        var (service, track, clock) = Granted();
        service.GetSnapshot();
        long before = service.EvaluationCount;
        clock.Advance(0.05);
        track.Occupy("3", true);
        service.ApplyFeedback(track.Observe());
        var snapshot = service.GetSnapshot();
        Assert.IsGreaterThan(before, service.EvaluationCount);
        Assert.AreEqual(OccupancyState.Occupied, snapshot.Blocks.Single(block => block.BlockId == "3").Occupancy);
        Assert.IsLessThan(150.0, Authority(snapshot), "The grant can no longer run into occupied block 3.");
    }

    [TestMethod]
    public void UnchangedHeartbeatFeedback_IsStillApplied()
    {
        var (service, track, clock) = Granted();
        clock.Advance(4.0);
        service.ApplyFeedback(track.Observe());
        clock.Advance(4.0);
        var snapshot = service.GetSnapshot();
        Assert.IsTrue(snapshot.Blocks.All(block => block.IsFresh), "The heartbeat 4 s ago refreshed every block.");
        Assert.IsGreaterThan(0, Authority(snapshot));
    }

    [TestMethod]
    public void StaleFeedback_WithdrawsAuthorityWithoutAnyNewInput()
    {
        var (service, _, clock) = Granted();
        var last = service.GetSnapshot();
        var feedbackExpiry = last.EvaluatedAtUtc.AddSeconds(5);

        // Polled continuously while the Track Model is silent: authority holds until the watchdog, then drops.
        while (clock.Now < feedbackExpiry.AddSeconds(-0.3))
        {
            clock.Advance(0.2);
            var snapshot = service.GetSnapshot();
            Assert.IsLessThanOrEqualTo(feedbackExpiry, snapshot.ValidUntilUtc, "A reused grant is never valid past its feedback's expiry.");
            Assert.IsGreaterThan(0, Authority(snapshot));
        }

        clock.Now = feedbackExpiry.AddMilliseconds(1);
        var stale = service.GetSnapshot();
        Assert.AreEqual(0, Authority(stale), "Withheld at the first poll after the feedback watchdog.");
        Assert.IsFalse(Origin(stale).IsFresh);
    }

    [TestMethod]
    public void ExpiredRequest_WithdrawsAuthorityWithoutAnyNewInput()
    {
        var (service, track, clock) = Granted();
        var requestExpiry = clock.Now.AddSeconds(10);
        while (clock.Now < requestExpiry.AddSeconds(-0.5))
        {
            clock.Advance(0.25);
            // Unchanged heartbeats keep the feedback fresh; CTC stopped renewing the request.
            service.ApplyFeedback(track.Observe());
            for (int poll = 0; poll < 3; poll++) Assert.IsGreaterThan(0, Authority(service.GetSnapshot()));
        }

        clock.Now = requestExpiry.AddSeconds(0.01);
        Assert.AreEqual(0, Authority(service.GetSnapshot()), "The request watchdog is evaluated without any new input.");
        StringAssert.Contains(Reason(service.GetSnapshot()), "expired");
    }

    [TestMethod]
    public void Cancellation_RevokesAuthorityImmediately()
    {
        var (service, _, _) = Granted();
        service.GetSnapshot();
        service.SubmitMovement(Request("r1", "1", ToStationB[1..], active: false));
        var snapshot = service.GetSnapshot();
        Assert.IsNull(Origin(snapshot).Request);
        Assert.AreEqual(0, snapshot.Blocks.Sum(block => block.AuthorizedAuthorityMeters));
    }

    /// <summary>
    /// Two identical controllers, one reusing evaluations and one evaluating on every request, driven through the
    /// same randomized life of a train (switch throws, signal clearing, crossing closing, block transitions, request
    /// renewals, lapses and cancellations, feedback silences, polls at random times): every snapshot must be identical.
    /// </summary>
    [TestMethod]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(3, false)]
    [DataRow(4, false)]
    [DataRow(5, false)]
    [DataRow(6, false)]
    [DataRow(7, true)]
    [DataRow(8, true)]
    public void Reuse_ProducesExactlyTheSnapshotsOfEvaluatingEveryTime(int seed, bool manual)
    {
        var random = new Random(seed);
        var clock = new Clock();
        var reusing = Controller(clock, manual: manual);
        var reference = Controller(clock, reuse: false, manual: manual);
        var track = new Track(reusing, clock);
        string[] route = seed % 2 == 0 ? ToStationC : ToStationB;
        int position = 0, requestNumber = 0, steps = 0;
        string requestId = "";
        bool renewing = true, silent = false;
        long reusedPolls = 0;

        void Both(Action<TrackControllerService> act)
        {
            Exception? first = null, second = null;
            try { act(reusing); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { first = ex; }
            try { act(reference); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { second = ex; }
            Assert.AreEqual(first?.Message, second?.Message);
        }

        WaysideSnapshotMessage Poll()
        {
            long before = reusing.EvaluationCount;
            var a = reusing.GetSnapshot();
            var b = reference.GetSnapshot();
            if (reusing.EvaluationCount == before) reusedPolls++;
            Assert.AreEqual(JsonSerializer.Serialize(b), JsonSerializer.Serialize(a), $"seed {seed}, step {steps}, t={clock.Now:HH:mm:ss.fff}");
            return a;
        }

        void Submit()
        {
            requestId = $"r{++requestNumber}";
            Both(service => service.SubmitMovement(Request(requestId, route[position], route[(position + 1)..])));
        }

        track.Occupy(route[0], true);
        Both(service => service.ApplyFeedback(track.Observe()));
        Submit();
        for (steps = 0; steps < 900 && position < route.Length - 1; steps++)
        {
            clock.Advance(random.NextDouble() * (random.Next(10) == 0 ? 2.5 : 0.4));
            int polls = random.Next(4);
            WaysideSnapshotMessage snapshot = Poll();
            for (int i = 0; i < polls; i++)
            {
                clock.Advance(random.NextDouble() * 0.05);
                snapshot = Poll();
            }

            track.Follow(snapshot);
            switch (random.Next(40))
            {
                case 0: silent = !silent; break; // Track Model stops (or resumes) reporting: feedback goes stale.
                case 1: renewing = !renewing; break; // CTC stops (or resumes) renewing: the request lapses.
                case 2:
                    Both(service => service.SubmitMovement(Request(requestId, route[position], route[(position + 1)..], active: false)));
                    Submit();
                    break;
            }

            if (renewing && random.Next(3) == 0)
            {
                string id = requestId;
                Both(service => service.SubmitMovement(Request(id, route[position], route[(position + 1)..])));
            }

            // The train moves on when its authority reaches beyond its block (enter the next, then leave this one).
            if (snapshot.Blocks.Single(block => block.BlockId == route[position]).AuthorizedAuthorityMeters > 50 && random.Next(3) == 0)
            {
                track.Occupy(route[position + 1], true);
                if (!silent) Both(service => service.ApplyFeedback(track.Observe()));
                clock.Advance(0.05);
                Poll();
                track.Occupy(route[position], false);
                position++;
                if (!silent) Both(service => service.ApplyFeedback(track.Observe()));
                Submit();
            }
            else if (!silent && random.Next(2) == 0)
            {
                Both(service => service.ApplyFeedback(track.Observe()));
            }
        }

        Console.WriteLine($"seed {seed}: {steps} steps, train reached index {position} of {route.Length - 1}, {reusedPolls} polls reused, "
            + $"{reusing.EvaluationCount} vs {reference.EvaluationCount} evaluations");
        Assert.IsGreaterThan(0, reusedPolls, "The scenario exercised reuse.");
        Assert.IsGreaterThan(2, position, "The scenario moved the train through several blocks.");
    }
}
