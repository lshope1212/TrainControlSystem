using CTC.Core.Dispatching;
using CTC.Core.Models;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Integration.Tests;

/// <summary>
/// CTC movement requests against the real Track Controller: complete route context, correlated
/// acknowledgements, actual authorization kept apart from delivery, safe rejection across a
/// territory boundary, renewal on wall time and expiry when CTC disappears. Physical release is
/// stood in for by a Train Model-style telemetry input to the real Track Model.
/// </summary>
[TestClass]
public sealed class LiveMovementIntegrationTests
{
    private static readonly TimeSpan Departure = new TimeSpan(9, 0, 30);

    private static ScheduledTrain BlueTrain(params string[] route)
    {
        var train = new ScheduledTrain { TrainId = "001", LineId = "Blue" };
        for (int i = 0; i < route.Length; i++)
        {
            train.Route.Add(new ScheduledRouteBlock { BlockId = route[i], ArrivalTime = i == 0 || i == route.Length - 1 ? Departure + TimeSpan.FromSeconds(30 * i) : null });
        }

        return train;
    }

    /// <summary>
    /// All modules up (the production Track Model starts with no trains), train 001 scheduled on <paramref name="route"/>,
    /// released by the shared clock's time, and placed in its start block via the Track Model.
    /// </summary>
    /// <param name="deployment">Prepares the Track Controller deployment (e.g. an edited copy of the samples); the single-territory Blue Line by default.</param>
    private static async Task<LiveSystem> PlacedTrainAsync(string[] route, Func<LiveSystem, string>? deployment = null, TimeSpan? renewal = null)
    {
        var system = new LiveSystem();
        await system.StartTrackControllerAsync(deployment?.Invoke(system));
        await system.StartCtcAsync(renewal);
        await system.StartTrackModelAsync();
        await LiveSystem.UntilAsync(() => system.OnCtc(ctc => ctc.State.FindLine("Blue") is not null), "CTC loads the layout.");

        await LiveSystem.UntilAsync(() => system.OnCtc(ctc => ctc.State.Lines.SelectMany(line => line.Blocks).All(block => block.Occupancy == OccupancyState.Clear)),
            "the line reports clear.");

        await system.OnCtc(ctc =>
        {
            ctc.QueueSchedule([BlueTrain(route)]);
            return 0;
        });
        await system.SendToCtcAsync(new SystemTimeMessage { SystemTime = Departure, SpeedMultiplier = 1, IsPaused = true });
        await LiveSystem.UntilAsync(() => system.OnCtc(ctc => ctc.State.FindDispatchedTrain("001")?.ReleaseStatus == TrainReleaseStatus.AwaitingPlacement),
            "the shared clock's time releases the train, awaiting placement.");
        Assert.IsEmpty(system.CtcSent!.Delivered.OfType<MovementSuggestionMessage>(), "Nothing is requested before the train is on the track.");
        Assert.IsEmpty(system.CtcSent.Delivered.OfType<MovementRequestMessage>(), "The unreceived release request is never sent.");

        // Physical placement is the Train Model's job; its telemetry input to the Track Model stands in for it.
        await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = "001", CurrentBlockId = route[0] });
        await LiveSystem.UntilAsync(() => system.OnCtc(ctc => ctc.State.FindDispatchedTrain("001")!.MovementRequest is { Delivery: MovementRequestDelivery.Delivered }),
            "CTC detects the train and issues its first movement request.");
        return system;
    }

    private static Task<MovementRequestState?> Request(LiveSystem system) =>
        system.OnCtc(ctc => ctc.State.FindDispatchedTrain("001")!.MovementRequest);

    private static async Task<bool> AcknowledgedAs(LiveSystem system, RequestAcknowledgementOutcome outcome) =>
        (await Request(system))?.Acknowledgement == outcome;

    [TestMethod]
    public async Task MovementRequestWithFullContext_IsAcceptedAndEvaluatedByTheTrackController()
    {
        await using var system = await PlacedTrainAsync(["1", "2", "3", "4", "5", "6", "7", "8", "9", "10"]);
        var request = (await Request(system))!;

        await LiveSystem.UntilAsync(() => AcknowledgedAs(system, RequestAcknowledgementOutcome.AcceptedForEvaluation), "the Track Controller accepts the request for evaluation.");
        await LiveSystem.UntilAsync(async () => (await Request(system))!.Authorization != MovementAuthorizationState.None, "the Track Controller reports its decision.");

        // The controller holds exactly CTC's route context.
        var held = system.WaysideBlock("1").Request!;
        Assert.AreEqual(request.RequestId, held.RequestId);
        Assert.AreEqual("001", held.TrainId);
        CollectionAssert.AreEqual(new[] { "2", "3", "4", "5", "6", "7", "8", "9", "10" }, held.PathBlockIds.ToArray());
        Assert.AreEqual(request.Plan.AuthorityMeters, held.SuggestedAuthorityMeters);

        // Paused controller: accepted is not authorized.
        var paused = (await Request(system))!;
        Assert.AreEqual(MovementAuthorizationState.Withheld, paused.Authorization);
        Assert.IsFalse(paused.IsMovementAuthorized);
        Assert.AreEqual(TrainReleaseStatus.DetectedOnTrack, await system.OnCtc(ctc => ctc.State.FindDispatchedTrain("001")!.ReleaseStatus));

        // Resumed: the controller re-evaluates. Whatever it decides, CTC reports exactly the authority
        // the controller actually commanded to the track (the Track Model's block 1), nothing more.
        await system.ResumeTrackControllerAsync();
        await Task.Delay(TimeSpan.FromSeconds(2));
        await LiveSystem.UntilAsync(async () =>
            {
                var decided = (await Request(system))!;
                double commanded = await system.OnTrackModel(track => track.FindBlock("1")!.AuthorityMeters);
                return Math.Abs(decided.AuthorizedAuthorityMeters - commanded) < 1e-6 && decided.IsMovementAuthorized == commanded > 0;
            },
            "CTC's reported authorization matches what the Track Controller commanded.");
    }

    [TestMethod]
    public async Task RouteWithoutSignals_IsAuthorizedEndToEnd_AndCtcReportsExactlyTheCommandedAuthority()
    {
        // Blocks 1-4 have no signal, so the grant needs no signal clearance (SignalledRouteIntegrationTests
        // covers routes through the signalled blocks 6 and 11).
        await using var system = await PlacedTrainAsync(["1", "2", "3", "4"]);
        await system.ResumeTrackControllerAsync();

        await LiveSystem.UntilAsync(async () => (await Request(system))!.IsMovementAuthorized, "the Track Controller grants the movement.");
        var granted = (await Request(system))!;
        Assert.IsGreaterThan(0.0, granted.AuthorizedAuthorityMeters);
        Assert.IsLessThanOrEqualTo(granted.Plan.AuthorityMeters, granted.AuthorizedAuthorityMeters, "Never more than CTC suggested.");
        await LiveSystem.UntilAsync(async () => Math.Abs(await system.OnTrackModel(track => track.FindBlock("1")!.AuthorityMeters) - granted.AuthorizedAuthorityMeters) < 1e-6,
            "the authority CTC reports is the authority actually commanded to the track.");
    }

    [TestMethod]
    public async Task CrossTerritoryRequest_IsRejectedSafely_AndNeverReportedAsAuthorized()
    {
        // The two-territory Blue Line split, both territories on this PC. Route 1..5, then branch C
        // (11 in one territory, 12-15 in the other).
        await using var live = await PlacedTrainAsync(["1", "2", "3", "4", "5", "11", "12", "13", "14", "15"], system =>
        {
            string folder = system.CopySamples(path => LiveSystem.EditJson(Path.Combine(path, "blue-line-pc-and-pi.deployment.json"), json =>
            {
                foreach (var territory in json["territories"]!.AsArray())
                {
                    territory!["execution"] = "Local";
                }
            }));
            return Path.Combine(folder, "blue-line-pc-and-pi.deployment.json");
        });

        await LiveSystem.UntilAsync(() => AcknowledgedAs(live, RequestAcknowledgementOutcome.Rejected), "the Track Controller rejects a request crossing its territories.");
        var request = (await Request(live))!;
        Assert.Contains("territory boundary", request.AcknowledgementDetail);
        Assert.IsFalse(request.IsMovementAuthorized);
        Assert.Contains("rejected by the Track Controller", await live.OnCtc(ctc => MovementRequestCoordinator.Describe(ctc.State.FindDispatchedTrain("001")!)));
        Assert.IsNull(live.WaysideBlock("1").Request, "Nothing was registered at the controller.");
    }

    [TestMethod]
    public async Task LaterAndReroutedRequests_CarryValidRouteContext()
    {
        await using var system = await PlacedTrainAsync(["1", "2", "3", "4", "5", "6", "7", "8", "9", "10"]);
        await LiveSystem.UntilAsync(() => AcknowledgedAs(system, RequestAcknowledgementOutcome.AcceptedForEvaluation), "first request accepted.");
        string firstId = (await Request(system))!.RequestId;

        // The train moves into block 2.
        await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = "001", CurrentBlockId = "2" });
        await LiveSystem.UntilAsync(async () => (await Request(system))?.Plan.TargetBlockId == "2", "CTC re-targets the request at the train's new block.");
        await LiveSystem.UntilAsync(() => AcknowledgedAs(system, RequestAcknowledgementOutcome.AcceptedForEvaluation), "the Track Controller accepts the new request.");
        var second = (await Request(system))!;
        Assert.AreNotEqual(firstId, second.RequestId);
        Assert.AreEqual("1", second.Plan.EntryBlockId);
        Assert.AreEqual(second.RequestId, system.WaysideBlock("2").Request!.RequestId);
        Assert.IsTrue(system.CtcSent!.Delivered.OfType<MovementSuggestionMessage>().Any(message => !message.IsActive && message.RequestId == firstId), "The old request was cancelled.");

        // The dispatcher re-routes the train to Station C (block 15).
        await system.OnCtcAsync(ctc => ctc.UpdateDispatchedTrainPlanAsync("001", "15", Departure + TimeSpan.FromMinutes(5)));
        await LiveSystem.UntilAsync(async () => (await Request(system))!.RequestId != second.RequestId, "the re-route gets a new request.");
        await LiveSystem.UntilAsync(() => AcknowledgedAs(system, RequestAcknowledgementOutcome.AcceptedForEvaluation), "the Track Controller accepts the re-routed request.");
        var rerouted = (await Request(system))!;
        Assert.AreEqual("2", rerouted.Plan.TargetBlockId);
        Assert.AreEqual("1", rerouted.Plan.EntryBlockId);
        CollectionAssert.AreEqual(new[] { "3", "4", "5", "11", "12", "13", "14", "15" }, rerouted.Plan.PathBlockIds.ToArray());
    }

    [TestMethod]
    public async Task Renewal_KeepsTheRequestAliveWithoutExtendingIt_AndItExpiresWhenCtcStops()
    {
        // A 3 s controller request watchdog and 1 s renewals, so the test runs in seconds.
        await using var live = await PlacedTrainAsync(
            ["1", "2", "3", "4", "5", "6", "7", "8", "9", "10"],
            system => Path.Combine(system.CopySamples(path => LiveSystem.EditJson(Path.Combine(path, "blue-line-track-map.json"), json => json["requestTimeoutSeconds"] = 3)), "blue-line-all-pc.deployment.json"),
            TimeSpan.FromSeconds(1));
        await LiveSystem.UntilAsync(() => AcknowledgedAs(live, RequestAcknowledgementOutcome.AcceptedForEvaluation), "request accepted.");
        await live.ResumeTrackControllerAsync();
        var original = live.WaysideBlock("1").Request!;

        // Twice the watchdog: renewals keep the very same request alive.
        await Task.Delay(TimeSpan.FromSeconds(6));
        var held = live.WaysideBlock("1");
        Assert.AreEqual(original.RequestId, held.Request!.RequestId);
        Assert.AreEqual(original.SuggestedAuthorityMeters, held.Request.SuggestedAuthorityMeters, "A renewal never extends the authority.");
        CollectionAssert.AreEqual(original.PathBlockIds, held.Request.PathBlockIds);
        Assert.DoesNotContain("expired", held.MovementReason);
        Assert.IsGreaterThanOrEqualTo(3, live.CtcSent!.Delivered.OfType<MovementSuggestionMessage>().Count(message => message.RequestId == original.RequestId && message.IsActive));

        // CTC disappears: no more renewals, and the controller's watchdog withdraws the request.
        await live.StopCtcAsync();
        await LiveSystem.UntilAsync(() => Task.FromResult(live.WaysideBlock("1").MovementReason.Contains("expired")), "the Track Controller expires the unrenewed request.", TimeSpan.FromSeconds(8));
        Assert.AreEqual(0, live.WaysideBlock("1").AuthorizedAuthorityMeters);
    }

    [TestMethod]
    public async Task RepeatedTrainUpdates_DoNotDuplicateRequests()
    {
        await using var system = await PlacedTrainAsync(["1", "2", "3", "4", "5", "6", "7", "8", "9", "10"]);

        for (int i = 0; i < 3; i++)
        {
            await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = "001", CurrentBlockId = "2" });
        }

        await LiveSystem.UntilAsync(async () => (await Request(system))?.Plan.TargetBlockId == "2", "CTC follows the train to block 2.");
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        var forBlock2 = system.CtcSent!.Delivered.OfType<MovementSuggestionMessage>().Where(message => message.IsActive && message.TargetBlockId == "2").Select(message => message.RequestId).Distinct().ToList();
        Assert.HasCount(1, forBlock2, "One movement into block 2, one request identity, however often the same state is reported.");
        Assert.AreEqual("2", await system.OnCtc(ctc => ctc.State.FindDispatchedTrain("001")!.CurrentBlockId));
    }
}
