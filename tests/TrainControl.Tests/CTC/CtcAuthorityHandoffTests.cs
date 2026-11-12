using CTC.Core.Dispatching;
using CTC.Core.Models;
using CTC.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.CTC;

/// <summary>
/// CTC's side of the block-to-block authority handoff: a successor is prepared only behind a grant
/// that reaches the next block, adopted (not re-issued) when the train enters it, withdrawn when the
/// grant is, and never a way to extend the movement's fixed end point. The Track Controller is played
/// by hand: acknowledgements and authorization reports as it would send them.
/// </summary>
[TestClass]
public class CtcAuthorityHandoffTests
{
    private static readonly TimeSpan Noon = new TimeSpan(12, 0, 0);
    private const double Length = BlueLine.BlockLengthMeters;

    private static async Task<(CTCService Service, FakeMessageSender Sender, ManualClock Clock)> PlacedTrainAsync(string[]? route = null)
    {
        var sender = new FakeMessageSender();
        var clock = new ManualClock();
        var service = new CTCService(sender, clock);
        service.ApplyTrackLayout(BlueLine.CreateLayout());
        BlueLine.ReportAllClear(service);
        route ??= BlueLine.BranchB;
        service.QueueSchedule([BlueLine.Train("001", Noon, secondsPerBlock: 4, route: route, timedBlocks: [route[0], route[^1]])]);
        await service.SetSystemTimeAsync(Noon);
        await BlueLine.PlaceTrainAsync(service, route[0]);
        return (service, sender, clock);
    }

    private static DispatchedTrainState Train(CTCService service) => service.State.DispatchedTrains.Single();

    private static List<MovementSuggestionMessage> Sent(FakeMessageSender sender) => sender.SentMessages.OfType<MovementSuggestionMessage>().ToList();

    private static void Acknowledge(CTCService service, MovementRequestState request, RequestAcknowledgementOutcome outcome, string detail = "") =>
        service.ApplyRequestAcknowledgement(new RequestAcknowledgementMessage
        {
            RequestId = request.RequestId,
            RequestType = nameof(MovementSuggestionMessage),
            Outcome = outcome,
            Detail = detail,
            BlockId = request.Plan.TargetBlockId,
            TrainId = "001",
        });

    /// <summary>The wayside's status of <paramref name="blockId"/> with its decision on <paramref name="request"/>.</summary>
    private static void Report(CTCService service, string blockId, OccupancyState occupancy, MovementRequestState? request = null, MovementAuthorizationState state = MovementAuthorizationState.Approved, double? authority = null, string superseded = "") =>
        service.ApplyBlockStatus(new BlockStatusMessage
        {
            BlockId = blockId,
            Occupancy = occupancy,
            Movement = request is null ? null : new MovementAuthorizationReport
            {
                RequestId = request.RequestId,
                TrainId = "001",
                State = state,
                AuthorizedSpeedMetersPerSecond = state == MovementAuthorizationState.Withheld ? 0 : request.Plan.SpeedMetersPerSecond,
                AuthorizedAuthorityMeters = state == MovementAuthorizationState.Withheld ? 0 : authority ?? request.Plan.AuthorityMeters,
                SupersededRequestId = superseded,
            },
        });

    /// <summary>The first request accepted and granted in full; CTC then prepares its successor.</summary>
    private static async Task<(CTCService Service, FakeMessageSender Sender, ManualClock Clock, MovementRequestState Active, MovementRequestState Staged)> GrantedAsync(string[]? route = null)
    {
        var (service, sender, clock) = await PlacedTrainAsync(route);
        var active = Train(service).MovementRequest!;
        Acknowledge(service, active, RequestAcknowledgementOutcome.AcceptedForEvaluation);
        Report(service, active.Plan.TargetBlockId, OccupancyState.Occupied, active);
        await service.SynchronizeMovementRequestsAsync();
        var staged = Train(service).StagedMovementRequest!;
        Assert.IsNotNull(staged, "A granted request reaching the next block gets a prepared successor.");
        Acknowledge(service, staged, RequestAcknowledgementOutcome.PreparedAsSuccessor, "Successor prepared.");
        return (service, sender, clock, active, staged);
    }

    /// <summary>The train enters <paramref name="to"/>; the wayside reports the promoted successor there.</summary>
    private static async Task EnterAsync(CTCService service, string from, string to, MovementRequestState? promoted, string superseded = "")
    {
        Report(service, to, OccupancyState.Occupied, promoted, superseded: superseded);
        BlueLine.Report(service, from, OccupancyState.Clear);
        await service.SynchronizeMovementRequestsAsync();
    }

    [TestMethod]
    public async Task Successor_IsPreparedBehindAGrant_WithTheSameEndPoint_AndIsNotAGrant()
    {
        var (service, sender, _, active, staged) = await GrantedAsync();

        var message = Sent(sender).Last();
        Assert.AreEqual(staged.RequestId, message.RequestId);
        Assert.AreEqual(active.RequestId, message.PredecessorRequestId);
        Assert.AreNotEqual(active.RequestId, message.RequestId);
        Assert.AreEqual("A2", message.TargetBlockId);
        Assert.AreEqual("A1", message.EntryBlockId);
        CollectionAssert.AreEqual(BlueLine.BranchB.Skip(2).ToArray(), message.PathBlockIds.ToArray());
        Assert.AreEqual(active.Plan.AuthorityMeters - Length, message.SuggestedAuthorityMeters, 1e-9, "Same end point from the next block's entry.");
        Assert.AreEqual(active.Plan.SpeedMetersPerSecond, message.SuggestedSpeedMetersPerSecond);

        Assert.AreEqual(active.RequestId, Train(service).MovementRequest!.RequestId, "The active request is unchanged.");
        Assert.IsFalse(staged.IsMovementAuthorized, "Prepared is not authorized.");
        Assert.Contains("prepared (not a grant", MovementRequestCoordinator.Describe(Train(service)));
    }

    [TestMethod]
    public async Task NoSuccessor_WithoutAGrantReachingTheNextBlock()
    {
        // Accepted but withheld.
        var (withheld, withheldSender, _) = await PlacedTrainAsync();
        var request = Train(withheld).MovementRequest!;
        Acknowledge(withheld, request, RequestAcknowledgementOutcome.AcceptedForEvaluation);
        Report(withheld, "A1", OccupancyState.Occupied, request, MovementAuthorizationState.Withheld);
        await withheld.SynchronizeMovementRequestsAsync();
        Assert.IsNull(Train(withheld).StagedMovementRequest);
        Assert.HasCount(1, Sent(withheldSender));

        // Granted, but only within the train's own block.
        var (restricted, restrictedSender, _) = await PlacedTrainAsync();
        var own = Train(restricted).MovementRequest!;
        Acknowledge(restricted, own, RequestAcknowledgementOutcome.AcceptedForEvaluation);
        Report(restricted, "A1", OccupancyState.Occupied, own, MovementAuthorizationState.Restricted, authority: Length);
        await restricted.SynchronizeMovementRequestsAsync();
        Assert.IsNull(Train(restricted).StagedMovementRequest);
        Assert.HasCount(1, Sent(restrictedSender));
    }

    [TestMethod]
    public async Task EnteringTheNextBlock_AdoptsThePreparedSuccessor_WithoutCancelAndReissue()
    {
        var (service, sender, _, active, staged) = await GrantedAsync();
        int before = Sent(sender).Count;

        await EnterAsync(service, "A1", "A2", staged, superseded: active.RequestId);

        var train = Train(service);
        Assert.AreSame(staged, train.MovementRequest, "The prepared request is now the active one.");
        Assert.IsTrue(train.MovementRequest!.IsMovementAuthorized);
        Assert.AreEqual(active.RequestId, train.MovementRequest.SupersededRequestId);
        Assert.Contains("handed over", MovementRequestCoordinator.Describe(train));

        var after = Sent(sender).Skip(before).ToList();
        Assert.IsFalse(after.Any(message => message.IsActive && message.TargetBlockId == "A2" && message.RequestId != staged.RequestId), "No new request is issued for block A2.");
        Assert.IsTrue(after.Any(message => !message.IsActive && message.RequestId == active.RequestId), "The predecessor is cancelled afterwards, as idempotent clean-up only.");

        // And the next successor (for A3) is prepared behind the now-active request.
        var next = train.StagedMovementRequest!;
        Assert.AreEqual("A3", next.Plan.TargetBlockId);
        Assert.AreEqual(staged.RequestId, next.PredecessorRequestId);
        Assert.AreEqual(staged.Plan.AuthorityMeters - Length, next.Plan.AuthorityMeters, 1e-9);

        // From now on the adopted request is renewed as an ordinary request.
        Assert.IsNull(train.MovementRequest.PredecessorRequestId);
    }

    [TestMethod]
    public async Task FixedEndPoint_ShrinksByEachBlock_AndIsNeverExtended()
    {
        var (service, _, _, active, staged) = await GrantedAsync();
        var observed = new List<double> { active.Plan.AuthorityMeters };
        var route = BlueLine.BranchB;
        for (int i = 1; i < 4; i++)
        {
            var successor = Train(service).StagedMovementRequest!;
            Acknowledge(service, successor, RequestAcknowledgementOutcome.PreparedAsSuccessor);
            await EnterAsync(service, route[i - 1], route[i], successor);
            observed.Add(Train(service).MovementRequest!.Plan.AuthorityMeters);
        }

        for (int i = 1; i < observed.Count; i++)
        {
            Assert.AreEqual(observed[i - 1] - Length, observed[i], 1e-9);
        }
    }

    [TestMethod]
    public async Task RevokedGrant_WithdrawsThePreparedSuccessor_AndTheTransitionFallsBackToANewRequest()
    {
        var (service, sender, _, active, staged) = await GrantedAsync();

        Report(service, "A1", OccupancyState.Occupied, active, MovementAuthorizationState.Withheld);
        await service.SynchronizeMovementRequestsAsync();
        Assert.IsNull(Train(service).StagedMovementRequest, "No successor stays prepared behind a withheld grant.");
        Assert.IsTrue(Sent(sender).Any(message => !message.IsActive && message.RequestId == staged.RequestId), "The successor is cancelled at the Track Controller.");

        await EnterAsync(service, "A1", "A2", promoted: null);
        var request = Train(service).MovementRequest!;
        Assert.AreNotEqual(staged.RequestId, request.RequestId, "The withdrawn successor is never adopted.");
        Assert.AreEqual("A2", request.Plan.TargetBlockId);
        Assert.IsFalse(request.IsMovementAuthorized, "Nothing is authorized until the Track Controller evaluates the new request.");
    }

    [TestMethod]
    public async Task RejectedSuccessor_IsNotRenewedOrAdopted()
    {
        var (service, sender, clock, _, staged) = await GrantedAsync();
        Acknowledge(service, staged, RequestAcknowledgementOutcome.Rejected, "A successor cannot move the predecessor's end point.");
        Assert.Contains("was rejected", MovementRequestCoordinator.Describe(Train(service)));

        clock.Advance(MovementRequestCoordinator.DefaultRenewalInterval);
        await service.SynchronizeMovementRequestsAsync();
        Assert.AreEqual(1, Sent(sender).Count(message => message.RequestId == staged.RequestId), "Not renewed.");

        await EnterAsync(service, "A1", "A2", promoted: null);
        Assert.AreNotEqual(staged.RequestId, Train(service).MovementRequest!.RequestId);
    }

    [TestMethod]
    public async Task LostPreparationAcknowledgement_DoesNotPreventAdoption()
    {
        var (service, _, _) = await PlacedTrainAsync();
        var active = Train(service).MovementRequest!;
        Acknowledge(service, active, RequestAcknowledgementOutcome.AcceptedForEvaluation);
        Report(service, "A1", OccupancyState.Occupied, active);
        await service.SynchronizeMovementRequestsAsync();
        var staged = Train(service).StagedMovementRequest!;

        // The staging acknowledgement never arrives; the wayside reports the promoted successor.
        await EnterAsync(service, "A1", "A2", staged, superseded: active.RequestId);

        Assert.AreSame(staged, Train(service).MovementRequest);
        Assert.IsTrue(staged.IsMovementAuthorized);
    }

    [TestMethod]
    public async Task StagedSuccessor_IsRenewedUnchanged_UnderTheWatchdog()
    {
        var (service, sender, clock, active, staged) = await GrantedAsync();
        clock.Advance(MovementRequestCoordinator.DefaultRenewalInterval);
        await service.SynchronizeMovementRequestsAsync();

        var renewals = Sent(sender).Where(message => message.RequestId == staged.RequestId).ToList();
        Assert.HasCount(2, renewals);
        Assert.AreEqual(active.RequestId, renewals[1].PredecessorRequestId);
        Assert.AreEqual(renewals[0].SuggestedAuthorityMeters, renewals[1].SuggestedAuthorityMeters);
        CollectionAssert.AreEqual(renewals[0].PathBlockIds, renewals[1].PathBlockIds);
    }

    [TestMethod]
    public async Task RepeatedOccupancyReports_DoNotDuplicateTheHandoff()
    {
        var (service, sender, _, active, staged) = await GrantedAsync();
        await EnterAsync(service, "A1", "A2", staged, superseded: active.RequestId);
        var next = Train(service).StagedMovementRequest!;
        int count = Sent(sender).Count;

        for (int i = 0; i < 3; i++)
        {
            await EnterAsync(service, "A1", "A2", staged, superseded: active.RequestId);
        }

        Assert.AreSame(staged, Train(service).MovementRequest);
        Assert.AreSame(next, Train(service).StagedMovementRequest);
        Assert.AreEqual("A2", Train(service).CurrentBlockId);
        Assert.HasCount(count, Sent(sender), "Nothing new is sent for a repeated state.");
    }

    [TestMethod]
    public async Task DispatcherReplan_CancelsThePreparedSuccessor_AndNeverAdoptsIt()
    {
        var (service, sender, _, active, staged) = await GrantedAsync();

        await service.UpdateDispatchedTrainPlanAsync("001", "C15", Noon + TimeSpan.FromMinutes(5));

        var train = Train(service);
        Assert.AreNotEqual(active.RequestId, train.MovementRequest!.RequestId, "A re-plan is a new movement with a new request.");
        Assert.AreNotEqual(staged.RequestId, train.MovementRequest.RequestId);
        Assert.IsNull(train.StagedMovementRequest);
        Assert.IsTrue(Sent(sender).Any(message => !message.IsActive && message.RequestId == staged.RequestId));
    }

    [TestMethod]
    public async Task FinalDestination_TerminatesTheRequestAndItsSuccessor()
    {
        var (service, sender, _, active, staged) = await GrantedAsync(["A1", "A2", "A3"]);
        await EnterAsync(service, "A1", "A2", staged, superseded: active.RequestId);
        var last = Train(service).StagedMovementRequest!;
        Acknowledge(service, last, RequestAcknowledgementOutcome.PreparedAsSuccessor);

        await EnterAsync(service, "A2", "A3", last, superseded: staged.RequestId);

        var train = Train(service);
        Assert.AreEqual(TrainMovementPhase.Completed, train.Phase);
        Assert.IsNull(train.MovementRequest);
        Assert.IsNull(train.StagedMovementRequest);
        Assert.IsTrue(Sent(sender).Any(message => !message.IsActive && message.RequestId == staged.RequestId));
        Assert.IsTrue(Sent(sender).Any(message => !message.IsActive && message.RequestId == last.RequestId), "The promoted final request is cancelled too.");
    }

    [TestMethod]
    public async Task FailedCleanupCancellation_IsRetriedOnTheNextPass()
    {
        var (service, sender, _, active, staged) = await GrantedAsync();
        sender.ExceptionToThrow = new IOException("pipe unavailable");

        await EnterAsync(service, "A1", "A2", staged, superseded: active.RequestId);
        Assert.AreSame(staged, Train(service).MovementRequest, "Adoption does not depend on the clean-up being delivered.");
        Assert.HasCount(1, Train(service).UncancelledRequests);

        sender.ExceptionToThrow = null;
        await service.SynchronizeMovementRequestsAsync();
        Assert.IsEmpty(Train(service).UncancelledRequests);
        Assert.IsTrue(Sent(sender).Any(message => !message.IsActive && message.RequestId == active.RequestId));
    }
}
