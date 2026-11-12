using CTC.Core.Dispatching;
using CTC.Core.Exceptions;
using CTC.Core.Models;
using CTC.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.CTC;

/// <summary>
/// CTC's half of physical dispatch: at release it sends a <see cref="TrainDispatchRequestMessage"/> to
/// the Track Controller (never to the Train Model, never as a movement request), keeps the train
/// awaiting placement until its start block reports occupied, retries with the same request ID until the
/// dispatch settles, and never treats a delivered or accepted dispatch as a physical placement.
/// </summary>
[TestClass]
public class CtcPhysicalDispatchTests
{
    private static TimeSpan At(int hours, int minutes, int seconds) => new(hours, minutes, seconds);

    private static (CTCService Service, FakeMessageSender Sender, ManualClock Clock) Create(params (string TrainId, TimeSpan Departure)[] trains)
    {
        var sender = new FakeMessageSender();
        var clock = new ManualClock();
        var service = new CTCService(sender, clock);
        // Test fixture only: a second yard entry at C11, so two trains can enter on different branches.
        // The production Blue Line has one yard entry (block 1); CTC accepts only starts its layout marks.
        service.ApplyTrackLayout(BlueLine.CreateLayout(yardEntries: ["A1", "C11"]));
        BlueLine.ReportAllClear(service);
        // 002 (when scheduled) starts on branch C, so the two trains never share a start block.
        service.QueueSchedule(trains.Select(train => BlueLine.Train(train.TrainId, train.Departure, secondsPerBlock: 4, route: train.TrainId == "002" ? SecondTrainRoute : null)));
        return (service, sender, clock);
    }

    private static readonly string[] SecondTrainRoute = ["C11", "C12", "C13", "C14", "C15"];

    private static List<TrainDispatchRequestMessage> Dispatches(FakeMessageSender sender) => sender.DispatchMessages.OfType<TrainDispatchRequestMessage>().ToList();

    private static TrainDispatchStatusMessage Status(TrainDispatchRequestMessage request, TrainDispatchStage stage, bool retryable = false, string by = "Track Model") => new()
    {
        RequestId = request.RequestId,
        RequestType = nameof(TrainDispatchRequestMessage),
        TrainId = request.TrainId,
        LineId = request.LineId,
        StartBlockId = request.StartBlockId,
        Stage = stage,
        IsRetryable = retryable,
        Detail = stage.ToString(),
        ReportedBy = by,
    };

    [TestMethod]
    public async Task Release_SendsOnePhysicalDispatch_AndNothingElse()
    {
        var (service, sender, _) = Create(("001", At(12, 0, 5)));

        await service.SetSystemTimeAsync(At(12, 0, 5));

        var dispatch = Dispatches(sender).Single();
        Assert.AreEqual("A1", dispatch.StartBlockId);
        Assert.IsEmpty(sender.SentMessages.OfType<MovementSuggestionMessage>(), "Dispatch is not movement authorization.");
        Assert.IsEmpty(sender.AllMessages.OfType<MovementRequestMessage>(), "The legacy label-only release intent is never sent.");
        var train = service.State.DispatchedTrains.Single();
        Assert.AreEqual(TrainReleaseStatus.AwaitingPlacement, train.ReleaseStatus, "Sending is not placing.");
        Assert.AreEqual(MovementRequestDelivery.Delivered, train.DispatchRequest!.Delivery);
        Assert.IsNull(train.DispatchRequest.Stage, "Delivery is not acceptance.");
    }

    [TestMethod]
    public async Task RepeatedClockTicks_NeverDuplicateTheTrainOrItsDispatch_AndRetriesKeepTheRequestId()
    {
        var (service, sender, clock) = Create(("001", At(12, 0, 5)));

        for (int i = 0; i < 20; i++)
        {
            await service.SetSystemTimeAsync(At(12, 0, 5) + TimeSpan.FromMilliseconds(100 * i));
            await service.SynchronizeMovementRequestsAsync();
        }

        Assert.HasCount(1, service.State.DispatchedTrains);
        Assert.HasCount(1, Dispatches(sender), "Not resent before the retry interval.");

        clock.Advance(TrainDispatchCoordinator.DefaultRetryInterval);
        await service.SynchronizeMovementRequestsAsync();

        Assert.HasCount(2, Dispatches(sender), "An unanswered dispatch is resent ...");
        Assert.AreEqual(Dispatches(sender)[0].RequestId, Dispatches(sender)[1].RequestId, "... with the same request ID, so the receivers treat it as one placement.");
        Assert.HasCount(1, service.State.DispatchedTrains);
    }

    [TestMethod]
    public async Task TrackControllerUnavailable_TheDispatchIsKeptAndRetried_NotLost()
    {
        var (service, sender, clock) = Create(("001", At(12, 0, 5)));
        sender.ExceptionToThrow = new MessageSendException("Track Controller is not connected.");
        var changes = new List<CtcStateChangedEventArgs>();
        service.StateChanged += (_, e) => changes.Add(e);

        await service.SetSystemTimeAsync(At(12, 0, 5));

        var train = service.State.DispatchedTrains.Single();
        Assert.IsEmpty(service.State.DispatchQueue, "The release decision stands; only the send failed.");
        Assert.AreEqual(MovementRequestDelivery.Failed, train.DispatchRequest!.Delivery);
        Assert.IsTrue(changes.Any(change => change.Kind == CtcStateChangeKind.TrainDispatchRequestUpdated && change.Message!.Contains("not delivered")),
            "The failure is reported, not swallowed.");

        sender.ExceptionToThrow = null;
        await service.SynchronizeMovementRequestsAsync();
        Assert.IsEmpty(Dispatches(sender), "Retries back off to the retry interval.");

        clock.Advance(TrainDispatchCoordinator.DefaultRetryInterval);
        await service.SetSystemTimeAsync(At(12, 0, 6));

        Assert.AreEqual(train.DispatchRequest.RequestId, Dispatches(sender).Single().RequestId);
        Assert.AreEqual(MovementRequestDelivery.Delivered, train.DispatchRequest.Delivery);
    }

    [TestMethod]
    public async Task Stages_AreRecordedButNeverPlaceTheTrain_OnlyStartBlockOccupancyDoes()
    {
        var (service, sender, clock) = Create(("001", At(12, 0, 5)));
        await service.SetSystemTimeAsync(At(12, 0, 5));
        var dispatch = Dispatches(sender).Single();
        var train = service.State.DispatchedTrains.Single();

        service.ApplyTrainDispatchStatus(Status(dispatch, TrainDispatchStage.ForwardedToTrackModel, by: "Track Controller"));
        service.ApplyTrainDispatchStatus(Status(dispatch, TrainDispatchStage.ActivationRequested));
        service.ApplyTrainDispatchStatus(Status(dispatch, TrainDispatchStage.ForwardedToTrackModel, by: "Track Controller"));
        Assert.AreEqual(TrainDispatchStage.ActivationRequested, train.DispatchRequest!.Stage, "A late, lower stage never moves the dispatch backwards.");

        service.ApplyTrainDispatchStatus(Status(dispatch, TrainDispatchStage.Placed));
        Assert.AreEqual(TrainDispatchStage.Placed, train.DispatchRequest.Stage);
        Assert.AreEqual(TrainReleaseStatus.AwaitingPlacement, train.ReleaseStatus, "CTC never takes identity or location from the Track Model's report.");
        Assert.IsNull(train.MovementRequest, "No movement request until CTC itself sees the train.");

        clock.Advance(TimeSpan.FromSeconds(30));
        await service.SynchronizeMovementRequestsAsync();
        Assert.HasCount(1, Dispatches(sender), "A placed dispatch is settled: not resent.");

        await BlueLine.PlaceTrainAsync(service, "A1");
        Assert.AreEqual(TrainReleaseStatus.DetectedOnTrack, train.ReleaseStatus);
        Assert.AreEqual("A1", sender.SentMessages.OfType<MovementSuggestionMessage>().Single().TargetBlockId, "Normal movement authorization starts.");
    }

    [TestMethod]
    public async Task StaleOrUnknownStatuses_AreIgnored()
    {
        var (service, sender, _) = Create(("001", At(12, 0, 5)));
        await service.SetSystemTimeAsync(At(12, 0, 5));
        var dispatch = Dispatches(sender).Single();
        var changes = new List<CtcStateChangedEventArgs>();
        service.StateChanged += (_, e) => changes.Add(e);

        service.ApplyTrainDispatchStatus(Status(new TrainDispatchRequestMessage { RequestId = "someone-else", TrainId = "001" }, TrainDispatchStage.Placed));
        service.ApplyTrainDispatchStatus(Status(new TrainDispatchRequestMessage { RequestId = "" }, TrainDispatchStage.Placed));

        Assert.IsNull(service.State.DispatchedTrains.Single().DispatchRequest!.Stage);
        Assert.IsEmpty(changes);
        Assert.AreEqual(dispatch.RequestId, service.State.DispatchedTrains.Single().DispatchRequest!.RequestId);
    }

    [TestMethod]
    public async Task RetryableRejection_IsRetried_ButAPermanentOneIsNot()
    {
        var (service, sender, clock) = Create(("001", At(12, 0, 5)));
        await service.SetSystemTimeAsync(At(12, 0, 5));
        var dispatch = Dispatches(sender).Single();

        service.ApplyTrainDispatchStatus(Status(dispatch, TrainDispatchStage.Rejected, retryable: true));
        clock.Advance(TrainDispatchCoordinator.DefaultRetryInterval);
        await service.SynchronizeMovementRequestsAsync();
        Assert.HasCount(2, Dispatches(sender), "e.g. the Train Model was not running: try again.");

        service.ApplyTrainDispatchStatus(Status(dispatch, TrainDispatchStage.Rejected, retryable: false));
        clock.Advance(TimeSpan.FromMinutes(1));
        await service.SynchronizeMovementRequestsAsync();
        Assert.HasCount(2, Dispatches(sender), "e.g. an invalid route: resending the same request cannot succeed.");
        Assert.Contains("refused by the Track Model", TrainDispatchCoordinator.Describe(service.State.DispatchedTrains.Single()));
    }

    [TestMethod]
    public async Task TwoTrains_AreDispatchedIndependently()
    {
        var (service, sender, _) = Create(("001", At(12, 0, 5)), ("002", At(12, 0, 5)));

        await service.SetSystemTimeAsync(At(12, 0, 5));

        var dispatches = Dispatches(sender);
        Assert.HasCount(2, dispatches);
        Assert.AreNotEqual(dispatches[0].RequestId, dispatches[1].RequestId);
        CollectionAssert.AreEquivalent(new[] { "001", "002" }, dispatches.Select(d => d.TrainId).ToArray());
        CollectionAssert.AreEqual(SecondTrainRoute, dispatches.Single(d => d.TrainId == "002").RouteBlockIds.ToArray());
        CollectionAssert.AreEqual(BlueLine.BranchB, dispatches.Single(d => d.TrainId == "001").RouteBlockIds.ToArray());
    }

    [TestMethod]
    public async Task ReplanBeforePlacement_SendsANewDispatchForTheNewRoute()
    {
        var (service, sender, _) = Create(("001", At(12, 0, 5)));
        await service.SetSystemTimeAsync(At(12, 0, 5));
        var first = Dispatches(sender).Single();

        await service.UpdateDispatchedTrainPlanAsync("001", "C13", At(12, 1, 0));

        var second = Dispatches(sender).Last();
        Assert.AreNotEqual(first.RequestId, second.RequestId, "A different plan is a different placement.");
        CollectionAssert.AreEqual(new[] { "A1", "A2", "A3", "A4", "A5", "C11", "C12", "C13" }, second.RouteBlockIds.ToArray());
    }

    [TestMethod]
    public async Task ReplanAfterPlacement_SendsARouteUpdateFromTheTrainsCurrentBlock()
    {
        var (service, sender, clock) = Create(("001", At(12, 0, 5)));
        await service.SetSystemTimeAsync(At(12, 0, 5));
        await BlueLine.PlaceTrainAsync(service, "A1");
        BlueLine.Report(service, "A2", OccupancyState.Occupied);

        await service.UpdateDispatchedTrainPlanAsync("001", "C12", At(12, 1, 0));

        var update = sender.DispatchMessages.OfType<TrainRouteUpdateMessage>().Single();
        Assert.AreEqual("001", update.TrainId);
        CollectionAssert.AreEqual(new[] { "A2", "A3", "A4", "A5", "C11", "C12" }, update.RouteBlockIds.ToArray());

        var train = service.State.DispatchedTrains.Single();
        service.ApplyTrainDispatchStatus(new TrainDispatchStatusMessage { RequestId = update.RequestId, TrainId = "001", Stage = TrainDispatchStage.RouteUpdated, ReportedBy = "Track Model" });
        clock.Advance(TimeSpan.FromMinutes(1));
        await service.SynchronizeMovementRequestsAsync();
        Assert.HasCount(1, sender.DispatchMessages.OfType<TrainRouteUpdateMessage>(), "An accepted route update is not resent.");
        Assert.AreEqual(TrainDispatchStage.RouteUpdated, train.RouteUpdate!.Stage);
    }

    [TestMethod]
    public async Task ProlongedOverlap_RepeatedOccupiedSnapshotsMoveTheTrainOnlyOnce()
    {
        var (service, _, _) = Create(("001", At(12, 0, 5)));
        await service.SetSystemTimeAsync(At(12, 0, 5));
        await BlueLine.PlaceTrainAsync(service, "A1");
        var train = service.State.DispatchedTrains.Single();

        // The front enters A2 while the rear still occupies A1: both report occupied, again and again.
        for (int i = 0; i < 5; i++)
        {
            BlueLine.Report(service, "A1", OccupancyState.Occupied);
            BlueLine.Report(service, "A2", OccupancyState.Occupied);
        }

        Assert.AreEqual("A2", train.CurrentBlockId);
        Assert.AreEqual(1, train.ActiveRouteIndex);
        Assert.AreEqual("A1", train.PreviousBlockId);

        BlueLine.Report(service, "A1", OccupancyState.Clear);
        Assert.AreEqual("A2", train.CurrentBlockId, "The rear clearing never moves the train.");
    }

    [TestMethod]
    public async Task ABlockThatWasOccupiedAndOnlyWentUnknown_IsNotANewEntry()
    {
        var (service, _, _) = Create(("001", At(12, 0, 5)));
        BlueLine.Report(service, "A2", OccupancyState.Occupied); // something else (e.g. another train's tail) is in A2
        await service.SetSystemTimeAsync(At(12, 0, 5));
        await BlueLine.PlaceTrainAsync(service, "A1");
        var train = service.State.DispatchedTrains.Single();

        // A2's track circuit (or the wayside's reports) drops out and comes back: still the same occupant.
        BlueLine.Report(service, "A2", OccupancyState.Unknown);
        BlueLine.Report(service, "A2", OccupancyState.Occupied);

        Assert.AreEqual("A1", train.CurrentBlockId, "Occupied -> Unknown -> Occupied shows nobody entering A2.");

        // A genuine entry still counts: A2 clears, then becomes occupied.
        BlueLine.Report(service, "A2", OccupancyState.Clear);
        BlueLine.Report(service, "A2", OccupancyState.Occupied);
        Assert.AreEqual("A2", train.CurrentBlockId);
    }

    [TestMethod]
    public async Task ABlockLastSeenClear_ThatComesBackOccupiedAfterAnOutage_IsStillAnEntry()
    {
        var (service, _, _) = Create(("001", At(12, 0, 5)));
        await service.SetSystemTimeAsync(At(12, 0, 5));
        await BlueLine.PlaceTrainAsync(service, "A1");

        BlueLine.Report(service, "A2", OccupancyState.Unknown);
        BlueLine.Report(service, "A2", OccupancyState.Occupied);

        Assert.AreEqual("A2", service.State.DispatchedTrains.Single().CurrentBlockId, "Clear -> (outage) -> Occupied is an entry, as before.");
    }
}
