using CTC.Core.Dispatching;
using CTC.Core.Models;
using CTC.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.CTC;

/// <summary>
/// The movement requests CTC holds at the Track Controller: complete route context on every
/// request, a new request (and cancellation of the old) when the train enters a new block, true
/// renewals that never extend the authority end, and acknowledgements/authorizations kept apart
/// from delivery.
/// </summary>
[TestClass]
public class CtcMovementRequestTests
{
    private static readonly TimeSpan Noon = new TimeSpan(12, 0, 0);

    /// <summary>
    /// Train 001 released on branch B (only A1 and B10 timed, so no intermediate stops) and placed in
    /// A1: it holds its first request. <paramref name="setup"/> runs before release.
    /// </summary>
    private static async Task<(CTCService Service, FakeMessageSender Sender, ManualClock Clock)> PlacedTrainAsync(Action<CTCService>? setup = null, string[]? route = null)
    {
        var sender = new FakeMessageSender();
        var clock = new ManualClock();
        var service = new CTCService(sender, clock);
        service.ApplyTrackLayout(BlueLine.CreateLayout());
        BlueLine.ReportAllClear(service);
        setup?.Invoke(service);
        route ??= BlueLine.BranchB;
        service.QueueSchedule([BlueLine.Train("001", Noon, secondsPerBlock: 4, route: route, timedBlocks: [route[0], route[^1]])]);
        await service.SetSystemTimeAsync(Noon);
        await BlueLine.PlaceTrainAsync(service, route[0]);
        return (service, sender, clock);
    }

    private static List<MovementSuggestionMessage> Sent(FakeMessageSender sender) => sender.SentMessages.OfType<MovementSuggestionMessage>().ToList();

    private static DispatchedTrainState Train(CTCService service) => service.State.DispatchedTrains.Single();

    /// <summary>The train moves from <paramref name="from"/> into <paramref name="to"/> (block-level), as the Track Model reports it.</summary>
    private static async Task MoveAsync(CTCService service, string from, string to)
    {
        BlueLine.Report(service, to, OccupancyState.Occupied);
        BlueLine.Report(service, from, OccupancyState.Clear);
        await service.SynchronizeMovementRequestsAsync();
    }

    private static RequestAcknowledgementMessage Ack(string requestId, RequestAcknowledgementOutcome outcome, string detail = "") => new RequestAcknowledgementMessage
    {
        RequestId = requestId,
        RequestType = nameof(MovementSuggestionMessage),
        Outcome = outcome,
        Detail = detail,
        BlockId = "A1",
        TrainId = "001",
    };

    [TestMethod]
    public async Task EnteringNextBlock_CancelsOldRequestThenIssuesNewOneWithFullContext()
    {
        var (service, sender, _) = await PlacedTrainAsync();
        var first = Sent(sender).Single();

        await MoveAsync(service, "A1", "A2");

        var sent = Sent(sender);
        Assert.HasCount(3, sent);
        var cancellation = sent[1];
        var second = sent[2];

        Assert.IsFalse(cancellation.IsActive);
        Assert.AreEqual(first.RequestId, cancellation.RequestId);
        Assert.AreEqual("A1", cancellation.TargetBlockId, "The cancellation names the old target, which is how the Track Controller finds it.");

        Assert.IsTrue(second.IsActive);
        Assert.AreNotEqual(first.RequestId, second.RequestId, "A new target needs a new request identity.");
        Assert.AreEqual("001", second.TrainId);
        Assert.AreEqual("A2", second.TargetBlockId);
        Assert.AreEqual("A1", second.EntryBlockId);
        Assert.AreEqual(MovementSuggestionMessage.BlockEntryReference, second.AuthorityReference);
        CollectionAssert.AreEqual(BlueLine.BranchB.Skip(2).ToArray(), second.PathBlockIds.ToArray());
        Assert.AreEqual(first.SuggestedSpeedMetersPerSecond, second.SuggestedSpeedMetersPerSecond);
        Assert.AreEqual(first.SuggestedAuthorityMeters - BlueLine.BlockLengthMeters, second.SuggestedAuthorityMeters, "Same end point, expressed from the new block's entry.");
        Assert.AreEqual(second.RequestId, Train(service).MovementRequest!.RequestId);
    }

    [TestMethod]
    public async Task EveryActiveRequest_CarriesCompleteRouteContext()
    {
        var (service, sender, _) = await PlacedTrainAsync();
        string[] route = BlueLine.BranchB;
        for (int i = 1; i < route.Length; i++)
        {
            await MoveAsync(service, route[i - 1], route[i]);
        }

        foreach (var request in Sent(sender).Where(request => request.IsActive))
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(request.RequestId));
            Assert.AreEqual("001", request.TrainId);
            Assert.IsFalse(string.IsNullOrWhiteSpace(request.TargetBlockId));
            Assert.AreEqual(MovementSuggestionMessage.BlockEntryReference, request.AuthorityReference);
            Assert.IsNotNull(request.PathBlockIds);
            Assert.IsGreaterThan(0.0, request.SuggestedSpeedMetersPerSecond);
            Assert.IsGreaterThan(0.0, request.SuggestedAuthorityMeters);
            int target = Array.IndexOf(route, request.TargetBlockId);
            Assert.AreEqual(target == 0 ? null : route[target - 1], request.EntryBlockId);
            CollectionAssert.AreEqual(route.Skip(target + 1).ToArray(), request.PathBlockIds.ToArray());
        }

        // Entering B10 completes the plan, so the last request was the one from B9.
        Assert.AreEqual("B9", Sent(sender).Last(request => request.IsActive).TargetBlockId);
        Assert.IsNull(Train(service).MovementRequest);
    }

    [TestMethod]
    public async Task Renewal_KeepsIdentityAndEveryLimit_OnlyWhenDue()
    {
        var (service, sender, clock) = await PlacedTrainAsync();
        var first = Sent(sender).Single();

        clock.Advance(MovementRequestCoordinator.DefaultRenewalInterval - TimeSpan.FromMilliseconds(1));
        await service.SynchronizeMovementRequestsAsync();
        Assert.HasCount(1, Sent(sender), "Not due yet: nothing is resent on every pass or clock tick.");

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await service.SynchronizeMovementRequestsAsync();

        var renewal = Sent(sender)[1];
        Assert.AreEqual(first.RequestId, renewal.RequestId);
        Assert.AreEqual(first.TargetBlockId, renewal.TargetBlockId);
        Assert.AreEqual(first.EntryBlockId, renewal.EntryBlockId);
        CollectionAssert.AreEqual(first.PathBlockIds, renewal.PathBlockIds);
        Assert.AreEqual(first.SuggestedSpeedMetersPerSecond, renewal.SuggestedSpeedMetersPerSecond);
        Assert.AreEqual(first.SuggestedAuthorityMeters, renewal.SuggestedAuthorityMeters);
        Assert.IsTrue(renewal.IsActive);
    }

    [TestMethod]
    public async Task Renewal_RunsOnWallTime_IndependentOfThePausedSimulation()
    {
        var (service, sender, clock) = await PlacedTrainAsync();

        // The shared clock is paused: the same time keeps arriving. Renewals follow wall time only.
        for (int i = 0; i < 10; i++)
        {
            await service.SetSystemTimeAsync(Noon);
        }

        Assert.HasCount(1, Sent(sender));

        clock.Advance(MovementRequestCoordinator.DefaultRenewalInterval);
        await service.SynchronizeMovementRequestsAsync();
        Assert.HasCount(2, Sent(sender));
        Assert.AreEqual(Sent(sender)[0].SuggestedAuthorityMeters, Sent(sender)[1].SuggestedAuthorityMeters, "Pausing never extends the authority.");
    }

    [TestMethod]
    public async Task AuthorityEnd_IsFixed_NeitherRenewalNorAdvanceExtendsIt()
    {
        // B7 occupied at release: the movement's authority ends at the end of B6 (300 m from A1).
        var (service, sender, clock) = await PlacedTrainAsync(service => BlueLine.Report(service, "B7", OccupancyState.Occupied));
        Assert.AreEqual(300.0, Sent(sender).Single().SuggestedAuthorityMeters);

        // B7 clears later. Neither a pass, a renewal nor entering a new block grants extra distance.
        BlueLine.Report(service, "B7", OccupancyState.Clear);
        await service.SynchronizeMovementRequestsAsync();
        clock.Advance(MovementRequestCoordinator.DefaultRenewalInterval);
        await service.SynchronizeMovementRequestsAsync();
        Assert.IsTrue(Sent(sender).All(request => request.SuggestedAuthorityMeters <= 300.0));

        await MoveAsync(service, "A1", "A2");
        var current = Sent(sender)[^1];
        Assert.AreEqual("A2", current.TargetBlockId);
        Assert.AreEqual(250.0, current.SuggestedAuthorityMeters);
        CollectionAssert.AreEqual(new[] { "A3", "A4", "A5", "B6" }, current.PathBlockIds.ToArray());
    }

    [TestMethod]
    public async Task HazardAhead_ShortensTheRequest_AndClearingRestoresOnlyUpToTheOriginalEnd()
    {
        var (service, sender, _) = await PlacedTrainAsync(service => BlueLine.Report(service, "B7", OccupancyState.Occupied));
        BlueLine.Report(service, "B7", OccupancyState.Clear);

        BlueLine.Report(service, "A3", OccupancyState.Unknown);
        await service.SynchronizeMovementRequestsAsync();
        var shortened = Sent(sender)[^1];
        Assert.IsTrue(shortened.IsActive);
        Assert.AreEqual(100.0, shortened.SuggestedAuthorityMeters, "Unknown occupancy ahead is never suggested.");
        CollectionAssert.AreEqual(new[] { "A2" }, shortened.PathBlockIds.ToArray());
        Assert.IsFalse(Sent(sender)[^2].IsActive, "The longer request was cancelled first.");

        BlueLine.Report(service, "A3", OccupancyState.Clear);
        await service.SynchronizeMovementRequestsAsync();
        var restored = Sent(sender)[^1];
        Assert.AreEqual(300.0, restored.SuggestedAuthorityMeters, "Back to the original end at B6, not beyond to B10.");
        Assert.AreNotEqual(shortened.RequestId, restored.RequestId);
    }

    [TestMethod]
    public async Task RepeatedOrOutOfOrderStatuses_NeverDuplicateMovement()
    {
        var (service, sender, clock) = await PlacedTrainAsync();
        var t0 = clock.GetUtcNow();

        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "A2", Occupancy = OccupancyState.Occupied, ReportedAtUtc = t0 + TimeSpan.FromSeconds(1) });
        await service.SynchronizeMovementRequestsAsync();
        int afterAdvance = Sent(sender).Count;

        for (int i = 0; i < 5; i++)
        {
            service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "A2", Occupancy = OccupancyState.Occupied, ReportedAtUtc = t0 + TimeSpan.FromSeconds(1) });
            await service.SynchronizeMovementRequestsAsync();
        }

        // A late "A2 clear" and a late "A3 occupied"? The first is ignored (older); the train is still in A2.
        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "A2", Occupancy = OccupancyState.Clear, ReportedAtUtc = t0 });
        await service.SynchronizeMovementRequestsAsync();

        Assert.HasCount(afterAdvance, Sent(sender));
        Assert.AreEqual("A2", Train(service).CurrentBlockId);
        Assert.AreEqual(1, Sent(sender).Count(request => request.IsActive && request.TargetBlockId == "A2"));
    }

    [TestMethod]
    public async Task RejectedRequest_IsNeverReportedAsAuthorized_AndIsNotRenewed()
    {
        var (service, sender, clock) = await PlacedTrainAsync();
        var request = Train(service).MovementRequest!;

        service.ApplyRequestAcknowledgement(Ack(request.RequestId, RequestAcknowledgementOutcome.Rejected, "Movement cannot cross a territory boundary until handoff coordination is implemented."));

        Assert.AreEqual(RequestAcknowledgementOutcome.Rejected, request.Acknowledgement);
        Assert.IsFalse(request.IsMovementAuthorized);
        Assert.AreEqual(TrainReleaseStatus.DetectedOnTrack, Train(service).ReleaseStatus, "Nothing claims a release beyond what occupancy shows.");
        Assert.Contains("rejected by the Track Controller", MovementRequestCoordinator.Describe(Train(service)));

        clock.Advance(TimeSpan.FromSeconds(30));
        await service.SynchronizeMovementRequestsAsync();
        Assert.HasCount(1, Sent(sender), "A rejected request is not renewed.");

        // A late authorization report for the rejected request does not resurrect it as authorized
        // unless the wayside really reports a grant for that exact ID.
        Assert.AreEqual(MovementAuthorizationState.None, request.Authorization);
    }

    [TestMethod]
    public async Task StaleOrRepeatedAcknowledgements_AreIgnored()
    {
        var (service, _, _) = await PlacedTrainAsync();
        var request = Train(service).MovementRequest!;
        var changes = new List<CtcStateChangedEventArgs>();
        service.StateChanged += (_, e) => changes.Add(e);

        service.ApplyRequestAcknowledgement(Ack("some-old-request", RequestAcknowledgementOutcome.Rejected, "late"));
        Assert.IsNull(request.Acknowledgement, "An acknowledgement for another request ID is stale.");

        service.ApplyRequestAcknowledgement(Ack(request.RequestId, RequestAcknowledgementOutcome.AcceptedForEvaluation, "Movement evaluated."));
        service.ApplyRequestAcknowledgement(Ack(request.RequestId, RequestAcknowledgementOutcome.AcceptedForEvaluation, "Movement evaluated."));

        Assert.AreEqual(RequestAcknowledgementOutcome.AcceptedForEvaluation, request.Acknowledgement);
        Assert.IsFalse(request.IsMovementAuthorized, "Accepted for evaluation is not an authority.");
        Assert.HasCount(1, changes.Where(change => change.Kind == CtcStateChangeKind.MovementRequestUpdated));
    }

    [TestMethod]
    public async Task AuthorizationReport_AppliesOnlyToTheRequestItNames()
    {
        var (service, _, clock) = await PlacedTrainAsync();
        var request = Train(service).MovementRequest!;

        service.ApplyBlockStatus(new BlockStatusMessage
        {
            BlockId = "A1",
            Occupancy = OccupancyState.Occupied,
            ReportedAtUtc = clock.GetUtcNow(),
            Movement = new MovementAuthorizationReport { RequestId = "another-request", State = MovementAuthorizationState.Approved, AuthorizedAuthorityMeters = 500, AuthorizedSpeedMetersPerSecond = 12 },
        });
        Assert.AreEqual(MovementAuthorizationState.None, request.Authorization);

        service.ApplyBlockStatus(new BlockStatusMessage
        {
            BlockId = "A1",
            Occupancy = OccupancyState.Occupied,
            ReportedAtUtc = clock.GetUtcNow() + TimeSpan.FromSeconds(1),
            Movement = new MovementAuthorizationReport { RequestId = request.RequestId, State = MovementAuthorizationState.Restricted, AuthorizedAuthorityMeters = 200, AuthorizedSpeedMetersPerSecond = 5, Reason = "Yellow aspect." },
        });
        Assert.AreEqual(MovementAuthorizationState.Restricted, request.Authorization);
        Assert.IsTrue(request.IsMovementAuthorized);
        Assert.AreEqual(200.0, request.AuthorizedAuthorityMeters);
        Assert.AreEqual(RequestAcknowledgementOutcome.AcceptedForEvaluation, request.Acknowledgement, "Being evaluated implies it was accepted.");

        // The wayside no longer holds the request (e.g. it expired): CTC stops showing an authority.
        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "A1", Occupancy = OccupancyState.Occupied, ReportedAtUtc = clock.GetUtcNow() + TimeSpan.FromSeconds(2) });
        Assert.AreEqual(MovementAuthorizationState.None, request.Authorization);
        Assert.IsFalse(request.IsMovementAuthorized);
    }

    [TestMethod]
    public async Task CompletedPlan_CancelsTheRequest()
    {
        var (service, sender, _) = await PlacedTrainAsync(route: ["A1", "A2", "A3"]);

        await MoveAsync(service, "A1", "A2");
        await MoveAsync(service, "A2", "A3");

        Assert.AreEqual(TrainMovementPhase.Completed, Train(service).Phase);
        Assert.IsNull(Train(service).MovementRequest);
        Assert.IsFalse(Sent(sender)[^1].IsActive);
    }

    [TestMethod]
    public async Task TrainAwaitingPlacement_HoldsNoRequest()
    {
        var sender = new FakeMessageSender();
        var service = BlueLine.CreateService(sender);
        service.QueueSchedule([BlueLine.Train("001", Noon)]);
        await service.SetSystemTimeAsync(Noon);

        await service.SynchronizeMovementRequestsAsync();

        Assert.IsEmpty(sender.SentMessages);
        Assert.IsNull(Train(service).MovementRequest);
        Assert.Contains("awaiting placement", MovementRequestCoordinator.Describe(Train(service)));
    }

    [TestMethod]
    public async Task TrainsOwnBlockClosed_CancelsAndHolds()
    {
        var (service, sender, _) = await PlacedTrainAsync();

        await service.CloseBlockAsync("A1");
        await service.SynchronizeMovementRequestsAsync();

        Assert.IsNull(Train(service).MovementRequest);
        Assert.IsFalse(Sent(sender)[^1].IsActive);
    }
}
