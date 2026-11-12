using CTC.Core.Dispatching;
using CTC.Core.Exceptions;
using CTC.Core.Interfaces;
using CTC.Core.Models;
using CTC.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.CTC;

/// <summary>
/// Release semantics: at its departure time a train whose start block is safe leaves the queue
/// awaiting placement and its physical dispatch goes to the Track Controller (never a movement
/// request); its first complete movement request goes to the Track Controller once its start block
/// reports occupied.
/// </summary>
[TestClass]
public class CtcDispatchTests
{
    /// <summary>Blue Line with one train per entry, each on branch B, entering a new block every 4 s.</summary>
    private static (CTCService Service, FakeMessageSender Sender) CreateService(params (string TrainId, TimeSpan Departure)[] trains)
    {
        var sender = new FakeMessageSender();
        var service = BlueLine.CreateService(sender);

        if (trains.Length > 0)
        {
            service.QueueSchedule(trains.Select(train => Train(train.TrainId, train.Departure)));
        }

        return (service, sender);
    }

    private static ScheduledTrain Train(string trainId, TimeSpan departure) => BlueLine.Train(trainId, departure, secondsPerBlock: 4);

    private static TimeSpan At(int hours, int minutes, int seconds) => new TimeSpan(hours, minutes, seconds);

    private static List<MovementSuggestionMessage> MovementSuggestions(FakeMessageSender sender) =>
        sender.SentMessages.OfType<MovementSuggestionMessage>().ToList();

    private static List<CtcStateChangedEventArgs> Record(CTCService service)
    {
        var changes = new List<CtcStateChangedEventArgs>();
        service.StateChanged += (_, e) => changes.Add(e);
        return changes;
    }

    [TestMethod]
    public async Task BeforeDeparture_NothingDispatched()
    {
        var (service, sender) = CreateService(("001", At(12, 0, 5)));

        await service.SetSystemTimeAsync(At(11, 59, 59));
        await service.SetSystemTimeAsync(At(12, 0, 4));

        Assert.IsEmpty(sender.AllMessages);
        Assert.HasCount(1, service.State.DispatchQueue);
        Assert.IsEmpty(service.State.DispatchedTrains);
    }

    [TestMethod]
    public async Task ExactDeparture_ReleasesTrainAwaitingPlacement_SendingOnlyItsPhysicalDispatch()
    {
        var (service, sender) = CreateService(("001", At(12, 0, 5)));

        await service.SetSystemTimeAsync(At(12, 0, 5));

        // The Track Controller authorizes movement only for a train it sees in the target block, so no
        // movement request yet: only the dispatch that has the train placed.
        Assert.IsEmpty(sender.SentMessages);
        var dispatch = (TrainDispatchRequestMessage)sender.DispatchMessages.Single();
        Assert.AreEqual("001", dispatch.TrainId);
        Assert.AreEqual("BLUE", dispatch.LineId);
        Assert.AreEqual("A1", dispatch.StartBlockId);
        CollectionAssert.AreEqual(BlueLine.BranchB.ToArray(), dispatch.RouteBlockIds.ToArray());
        Assert.IsFalse(string.IsNullOrWhiteSpace(dispatch.RequestId));
        Assert.IsEmpty(service.State.DispatchQueue);

        var dispatched = service.State.DispatchedTrains.Single();
        Assert.AreEqual("001", dispatched.TrainId);
        Assert.AreEqual("BLUE", dispatched.LineId);
        Assert.AreEqual("A1", dispatched.CurrentBlockId);
        Assert.AreEqual(TrainReleaseStatus.AwaitingPlacement, dispatched.ReleaseStatus);
        Assert.IsNull(dispatched.MovementRequest);
    }

    [TestMethod]
    public async Task PlacedTrain_FirstRequestCarriesCompleteRouteContext()
    {
        var (service, sender) = CreateService(("001", At(12, 0, 5)));
        await service.SetSystemTimeAsync(At(12, 0, 5));
        var changes = Record(service);

        await BlueLine.PlaceTrainAsync(service, "A1");

        var suggestion = MovementSuggestions(sender).Single();
        Assert.IsFalse(string.IsNullOrWhiteSpace(suggestion.RequestId));
        Assert.AreEqual("001", suggestion.TrainId);
        Assert.AreEqual("A1", suggestion.TargetBlockId, "The target is the block the train is in.");
        Assert.IsNull(suggestion.EntryBlockId, "At its start block the train entered from nowhere on the route.");
        Assert.AreEqual(MovementSuggestionMessage.BlockEntryReference, suggestion.AuthorityReference);
        CollectionAssert.AreEqual(BlueLine.BranchB.Skip(1).ToArray(), suggestion.PathBlockIds.ToArray());
        Assert.AreEqual(12.5, suggestion.SuggestedSpeedMetersPerSecond, 1e-9);
        Assert.AreEqual(500.0, suggestion.SuggestedAuthorityMeters);
        Assert.IsTrue(suggestion.IsActive);

        var train = service.State.DispatchedTrains.Single();
        Assert.AreEqual(TrainReleaseStatus.DetectedOnTrack, train.ReleaseStatus);
        Assert.AreEqual(suggestion.RequestId, train.MovementRequest!.RequestId);
        Assert.AreEqual(MovementRequestDelivery.Delivered, train.MovementRequest.Delivery);
        Assert.IsNull(train.MovementRequest.Acknowledgement, "Delivery is not acceptance.");
        Assert.IsTrue(changes.Any(change => change.Kind == CtcStateChangeKind.TrainDetectedOnTrack && change.TrainId == "001"));
    }

    [TestMethod]
    public async Task Release_NeverSendsTheUnreceivedReleaseRequest()
    {
        var (service, sender) = CreateService(("001", At(12, 0, 5)));

        await service.SetSystemTimeAsync(At(12, 0, 5));
        await BlueLine.PlaceTrainAsync(service, "A1");
        await service.SetSystemTimeAsync(At(12, 0, 6));

        Assert.IsEmpty(sender.SentMessages.OfType<MovementRequestMessage>());
    }

    [TestMethod]
    public async Task Dispatch_SparseSchedule_UsesWholeRoutedPath()
    {
        // Only A1 and B10 timed: speed targets B10 (450 m in 36 s) and authority covers the routed-through blocks.
        var sender = new FakeMessageSender();
        var service = BlueLine.CreateService(sender);
        service.QueueSchedule([BlueLine.Train("001", At(12, 0, 5), secondsPerBlock: 4, timedBlocks: ["A1", "B10"])]);

        await service.SetSystemTimeAsync(At(12, 0, 5));
        await BlueLine.PlaceTrainAsync(service, "A1");

        var suggestion = MovementSuggestions(sender).Single();
        Assert.AreEqual(12.5, suggestion.SuggestedSpeedMetersPerSecond, 1e-9);
        Assert.AreEqual(500.0, suggestion.SuggestedAuthorityMeters);
        CollectionAssert.AreEqual(BlueLine.BranchB.Skip(1).ToArray(), suggestion.PathBlockIds.ToArray());
    }

    [TestMethod]
    public async Task Dispatch_StoresSuggestedSpeedAndAuthority()
    {
        var (service, sender) = CreateService(("001", At(12, 0, 5)));
        BlueLine.Report(service, "A4", OccupancyState.Occupied);

        await service.SetSystemTimeAsync(At(12, 0, 5));

        var dispatched = service.State.DispatchedTrains.Single();
        Assert.AreEqual(12.5, dispatched.SuggestedSpeedMetersPerSecond, 1e-9);
        Assert.AreEqual(150.0, dispatched.SuggestedAuthorityMeters);

        await BlueLine.PlaceTrainAsync(service, "A1");
        var suggestion = MovementSuggestions(sender).Single();
        Assert.AreEqual(suggestion.SuggestedSpeedMetersPerSecond, dispatched.SuggestedSpeedMetersPerSecond);
        Assert.AreEqual(suggestion.SuggestedAuthorityMeters, dispatched.SuggestedAuthorityMeters);
        CollectionAssert.AreEqual(new[] { "A2", "A3" }, suggestion.PathBlockIds.ToArray(), "The path covers only the blocks the authority covers.");
    }

    [TestMethod]
    public async Task Dispatch_OccupiedRouteBlock_LimitsAuthority()
    {
        var (service, sender) = CreateService(("001", At(12, 0, 5)));
        BlueLine.Report(service, "A3", OccupancyState.Occupied);

        await service.SetSystemTimeAsync(At(12, 0, 5));
        await BlueLine.PlaceTrainAsync(service, "A1");

        Assert.AreEqual(100.0, MovementSuggestions(sender).Single().SuggestedAuthorityMeters);
    }

    [TestMethod]
    public async Task Dispatch_UnknownRouteBlock_LimitsAuthority()
    {
        var (service, sender) = CreateService(("001", At(12, 0, 5)));
        BlueLine.Report(service, "A3", OccupancyState.Unknown);

        await service.SetSystemTimeAsync(At(12, 0, 5));
        await BlueLine.PlaceTrainAsync(service, "A1");

        Assert.AreEqual(100.0, MovementSuggestions(sender).Single().SuggestedAuthorityMeters, "Unknown occupancy is as unsafe as occupied.");
    }

    [TestMethod]
    public async Task Dispatch_MaintenanceClosedRouteBlock_LimitsAuthority()
    {
        var (service, sender) = CreateService(("001", At(12, 0, 5)));
        await service.CloseBlockAsync("B7");
        sender.SentMessages.Clear();

        await service.SetSystemTimeAsync(At(12, 0, 5));
        await BlueLine.PlaceTrainAsync(service, "A1");

        // A1-A5 and B6 are usable: 6 x 50 m.
        Assert.AreEqual(300.0, MovementSuggestions(sender).Single().SuggestedAuthorityMeters);
    }

    [TestMethod]
    public async Task Dispatch_BlockOnOtherBranch_DoesNotLimitAuthority()
    {
        var (service, sender) = CreateService(("001", At(12, 0, 5)));
        BlueLine.Report(service, "C11", OccupancyState.Occupied);

        await service.SetSystemTimeAsync(At(12, 0, 5));
        await BlueLine.PlaceTrainAsync(service, "A1");

        Assert.AreEqual(500.0, MovementSuggestions(sender).Single().SuggestedAuthorityMeters);
    }

    [TestMethod]
    [DataRow(OccupancyState.Occupied)]
    [DataRow(OccupancyState.Unknown)]
    public async Task Dispatch_UnsafeStartBlock_KeepsTrainQueuedWithoutSending(OccupancyState startBlock)
    {
        var (service, sender) = CreateService(("001", At(12, 0, 5)));
        BlueLine.Report(service, "A1", startBlock);
        var changes = Record(service);

        await service.SetSystemTimeAsync(At(12, 0, 5));

        Assert.IsEmpty(sender.AllMessages, "An unsafe start block is not dispatched into.");
        Assert.AreEqual(DispatchQueueStatus.Queued, service.State.DispatchQueue.Single().QueueStatus);
        Assert.IsEmpty(service.State.DispatchedTrains);
        var failure = changes.Single(change => change.Kind == CtcStateChangeKind.DispatchFailed);
        Assert.AreEqual("its start block A1 is occupied, has unknown occupancy, or is closed for maintenance.", failure.Message);

        // Once the block reports clear, the next time update releases the train.
        BlueLine.Report(service, "A1", OccupancyState.Clear);
        await service.SetSystemTimeAsync(At(12, 0, 6));

        Assert.HasCount(1, service.State.DispatchedTrains);
        Assert.IsEmpty(service.State.DispatchQueue);
    }

    [TestMethod]
    public async Task Dispatch_NoStatusYet_KeepsTrainQueued()
    {
        // A layout alone says nothing about occupancy: CTC must not release into unknown track.
        var sender = new FakeMessageSender();
        var service = new CTCService(sender);
        service.ApplyTrackLayout(BlueLine.CreateLayout());
        service.QueueSchedule([Train("001", At(12, 0, 5))]);

        await service.SetSystemTimeAsync(At(12, 0, 5));

        Assert.IsEmpty(service.State.DispatchedTrains);
        Assert.AreEqual(DispatchQueueStatus.Queued, service.State.DispatchQueue.Single().QueueStatus);
        Assert.IsEmpty(sender.AllMessages);
    }

    [TestMethod]
    public async Task LateDispatch_UsesPermittedMaximumAndWarns()
    {
        // Next block (A2) was due at 12:00:09; released at 12:00:10.
        var (service, sender) = CreateService(("001", At(12, 0, 5)));
        var changes = Record(service);

        await service.SetSystemTimeAsync(At(12, 0, 10));
        await BlueLine.PlaceTrainAsync(service, "A1");

        Assert.AreEqual(50.0 / 3.6, MovementSuggestions(sender).Single().SuggestedSpeedMetersPerSecond, 1e-9);
        var dispatched = changes.Single(change => change.Kind == CtcStateChangeKind.TrainDispatched);
        Assert.IsNotNull(dispatched.Message);
        Assert.Contains("behind schedule for A2", dispatched.Message);
    }

    [TestMethod]
    public async Task OnTimeDispatch_HasNoWarning()
    {
        var (service, _) = CreateService(("001", At(12, 0, 5)));
        var changes = Record(service);

        await service.SetSystemTimeAsync(At(12, 0, 5));

        Assert.IsNull(changes.Single(change => change.Kind == CtcStateChangeKind.TrainDispatched).Message);
    }

    [TestMethod]
    public async Task LaterTicks_DoNotDispatchAgain()
    {
        var (service, sender) = CreateService(("001", At(12, 0, 5)));
        var changes = Record(service);

        await service.SetSystemTimeAsync(At(12, 0, 5));
        await BlueLine.PlaceTrainAsync(service, "A1");
        await service.SetSystemTimeAsync(At(12, 0, 6));
        await service.SetSystemTimeAsync(At(12, 0, 7));
        await service.SynchronizeMovementRequestsAsync();

        Assert.HasCount(1, changes.Where(change => change.Kind == CtcStateChangeKind.TrainDispatched));
        Assert.HasCount(1, MovementSuggestions(sender), "Neither ticks nor a repeated pass resend a request that is not yet due for renewal.");
        Assert.HasCount(1, service.State.DispatchedTrains);
    }

    [TestMethod]
    public async Task ClockJump_DispatchesAllDueTrainsInDepartureOrder()
    {
        // Queued out of order on purpose.
        var (service, _) = CreateService(
            ("003", At(12, 0, 6)),
            ("001", At(12, 0, 2)),
            ("002", At(12, 0, 4)));
        await service.SetSystemTimeAsync(At(12, 0, 0));
        var changes = Record(service);

        await service.SetSystemTimeAsync(At(12, 0, 10));

        CollectionAssert.AreEqual(
            new[] { "001", "002", "003" },
            changes.Where(change => change.Kind == CtcStateChangeKind.TrainDispatched).Select(change => change.TrainId).ToArray());
        Assert.IsEmpty(service.State.DispatchQueue);
        Assert.HasCount(3, service.State.DispatchedTrains);
    }

    [TestMethod]
    public async Task FutureTrain_StaysQueued()
    {
        var (service, _) = CreateService(("001", At(12, 0, 5)), ("002", At(12, 1, 0)));

        await service.SetSystemTimeAsync(At(12, 0, 10));

        Assert.AreEqual("001", service.State.DispatchedTrains.Single().TrainId);
        var queued = service.State.DispatchQueue.Single();
        Assert.AreEqual("002", queued.TrainId);
        Assert.AreEqual(DispatchQueueStatus.Queued, queued.QueueStatus);
    }

    [TestMethod]
    public async Task FailedRequestSend_IsRecordedAndRetriedWithTheSameRequestId()
    {
        var sender = new FakeMessageSender();
        var clock = new ManualClock();
        var service = new CTCService(sender, clock);
        service.ApplyTrackLayout(BlueLine.CreateLayout());
        BlueLine.ReportAllClear(service);
        service.QueueSchedule([Train("001", At(12, 0, 5))]);
        await service.SetSystemTimeAsync(At(12, 0, 5));
        sender.ExceptionToThrow = new MessageSendException("Track Controller is not connected.");
        var changes = Record(service);

        await BlueLine.PlaceTrainAsync(service, "A1");

        var train = service.State.DispatchedTrains.Single();
        var failed = train.MovementRequest!;
        Assert.AreEqual(MovementRequestDelivery.Failed, failed.Delivery);
        Assert.AreEqual("Track Controller is not connected.", failed.DeliveryError);
        var report = changes.Single(change => change.Kind == CtcStateChangeKind.MovementRequestUpdated);
        Assert.Contains("not delivered", report.Message!);

        sender.ExceptionToThrow = null;
        await service.SynchronizeMovementRequestsAsync();
        Assert.IsEmpty(MovementSuggestions(sender), "Periodic passes back off: an unreachable Track Controller is not retried on every pass.");

        clock.Advance(MovementRequestCoordinator.DefaultRenewalInterval);
        await service.SynchronizeMovementRequestsAsync();

        var suggestion = MovementSuggestions(sender).Single();
        Assert.AreEqual(failed.RequestId, suggestion.RequestId, "A retry is the same request, so it can never duplicate a movement.");
        Assert.AreEqual(MovementRequestDelivery.Delivered, train.MovementRequest!.Delivery);
    }

    [TestMethod]
    public async Task UnsafeStartForOneTrain_DoesNotStopOtherDueTrains()
    {
        var sender = new FakeMessageSender();
        var service = BlueLine.CreateService(sender);
        service.QueueSchedule([Train("001", At(12, 0, 2)), BlueLine.Train("002", At(12, 0, 4), route: ["C11", "C12", "C13"])]);
        BlueLine.Report(service, "A1", OccupancyState.Occupied);

        await service.SetSystemTimeAsync(At(12, 0, 10));

        Assert.AreEqual("001", service.State.DispatchQueue.Single().TrainId);
        Assert.AreEqual("002", service.State.DispatchedTrains.Single().TrainId);
    }

    [TestMethod]
    public async Task QueueSchedule_DoesNotRequeueDispatchedTrain()
    {
        var (service, _) = CreateService(("001", At(12, 0, 5)), ("002", At(12, 0, 10)));
        await service.SetSystemTimeAsync(At(12, 0, 5));
        var changes = Record(service);

        service.QueueSchedule([Train("001", At(12, 0, 5)), Train("002", At(12, 0, 10))]);
        await service.SetSystemTimeAsync(At(12, 0, 6));

        Assert.AreEqual("002", service.State.DispatchQueue.Single().TrainId);
        Assert.AreEqual("001", service.State.DispatchedTrains.Single().TrainId);
        Assert.IsFalse(changes.Any(change => change.Kind == CtcStateChangeKind.TrainDispatched));
    }

    [TestMethod]
    public async Task ClockMovingBackward_DoesNotUndoDispatch()
    {
        var (service, _) = CreateService(("001", At(12, 0, 5)), ("002", At(12, 0, 10)));
        await service.SetSystemTimeAsync(At(12, 0, 5));

        await service.SetSystemTimeAsync(At(11, 59, 0));

        Assert.AreEqual("001", service.State.DispatchedTrains.Single().TrainId);
        Assert.AreEqual("002", service.State.DispatchQueue.Single().TrainId);
    }

    [TestMethod]
    public async Task SetSystemTimeAsync_StoresTime()
    {
        var (service, _) = CreateService();

        await service.SetSystemTimeAsync(At(12, 34, 56));

        Assert.AreEqual(At(12, 34, 56), service.State.SystemTime);
    }

    [TestMethod]
    public async Task Dispatch_RaisesSystemTimeThenTrainDispatched_ThenItsDispatchIsSent()
    {
        var (service, _) = CreateService(("001", At(12, 0, 5)));
        var changes = Record(service);

        await service.SetSystemTimeAsync(At(12, 0, 5));

        CollectionAssert.AreEqual(
            new[] { CtcStateChangeKind.SystemTime, CtcStateChangeKind.TrainDispatched, CtcStateChangeKind.TrainDispatchRequestUpdated },
            changes.Select(change => change.Kind).ToArray());
        Assert.AreEqual("001", changes[1].TrainId);
        Assert.AreEqual("001", changes[2].TrainId);
        Assert.Contains("delivered to the Track Controller", changes[2].Message!);
    }

    [TestMethod]
    public async Task OverlappingSynchronization_SendsTheRequestOnlyOnce()
    {
        // A second pass (another status, the renewal timer, a time update) can run while the first
        // is still awaiting its send; the request must not be sent twice.
        var sender = new BlockingSender();
        var service = BlueLine.CreateService(sender);
        service.QueueSchedule([Train("001", At(12, 0, 5))]);
        await service.SetSystemTimeAsync(At(12, 0, 5));

        BlueLine.Report(service, "A1", OccupancyState.Occupied);
        var first = service.SynchronizeMovementRequestsAsync();
        var second = service.SynchronizeMovementRequestsAsync();
        await service.SetSystemTimeAsync(At(12, 0, 6));
        sender.Release();
        await Task.WhenAll(first, second);

        Assert.AreEqual(1, sender.Sent.OfType<MovementSuggestionMessage>().Count());
        Assert.HasCount(1, service.State.DispatchedTrains);
    }

    /// <summary>Holds every send open until <see cref="Release"/> is called.</summary>
    private sealed class BlockingSender : IMessageSender
    {
        private readonly TaskCompletionSource _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<object> Sent { get; } = new List<object>();

        public void Release() => _release.SetResult();

        public Task SendAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
            where TMessage : class
        {
            Sent.Add(message);

            // Only movement requests are held: this test is about overlapping movement synchronization.
            return message is MovementSuggestionMessage ? _release.Task : Task.CompletedTask;
        }
    }
}
