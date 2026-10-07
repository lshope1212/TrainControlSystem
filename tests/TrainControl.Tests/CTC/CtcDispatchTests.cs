using CTC.Core.Exceptions;
using CTC.Core.Interfaces;
using CTC.Core.Models;
using CTC.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.CTC;

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

    private static List<MovementRequestMessage> MovementRequests(FakeMessageSender sender) =>
        sender.SentMessages.OfType<MovementRequestMessage>().ToList();

    private static List<MovementSuggestionMessage> MovementSuggestions(FakeMessageSender sender) =>
        sender.SentMessages.OfType<MovementSuggestionMessage>().ToList();

    [TestMethod]
    public async Task BeforeDeparture_NothingDispatched()
    {
        var (service, sender) = CreateService(("Train 1", At(12, 0, 5)));

        await service.SetSystemTimeAsync(At(11, 59, 59));
        await service.SetSystemTimeAsync(At(12, 0, 4));

        Assert.IsEmpty(sender.SentMessages);
        Assert.HasCount(1, service.State.DispatchQueue);
        Assert.IsEmpty(service.State.DispatchedTrains);
    }

    [TestMethod]
    public async Task ExactDeparture_SendsSuggestionThenReleaseAndMovesTrain()
    {
        var (service, sender) = CreateService(("Train 1", At(12, 0, 5)));

        await service.SetSystemTimeAsync(At(12, 0, 5));

        // Suggestion first, so the Track Controller has movement data before the release.
        Assert.HasCount(2, sender.SentMessages);
        var suggestion = (MovementSuggestionMessage)sender.SentMessages[0];
        var request = (MovementRequestMessage)sender.SentMessages[1];

        Assert.AreEqual("Train 1", suggestion.TrainId);
        Assert.AreEqual(12.5, suggestion.SuggestedSpeedMetersPerSecond, 1e-9);
        Assert.AreEqual(500.0, suggestion.SuggestedAuthorityMeters);
        Assert.AreEqual("Train 1", request.TrainId);
        Assert.AreEqual(MovementRequestType.ReleaseTrain, request.RequestType);
        Assert.IsEmpty(service.State.DispatchQueue);

        var dispatched = service.State.DispatchedTrains.Single();
        Assert.AreEqual("Train 1", dispatched.TrainId);
        Assert.AreEqual("BLUE", dispatched.LineId);
        Assert.AreEqual("A1", dispatched.CurrentBlockId);
    }

    [TestMethod]
    public async Task Dispatch_StoresSuggestedSpeedAndAuthority()
    {
        var (service, sender) = CreateService(("Train 1", At(12, 0, 5)));
        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "A4", Occupancy = OccupancyState.Occupied });

        await service.SetSystemTimeAsync(At(12, 0, 5));

        var dispatched = service.State.DispatchedTrains.Single();
        var suggestion = MovementSuggestions(sender).Single();
        Assert.AreEqual(12.5, dispatched.SuggestedSpeedMetersPerSecond, 1e-9);
        Assert.AreEqual(150.0, dispatched.SuggestedAuthorityMeters);
        Assert.AreEqual(suggestion.SuggestedSpeedMetersPerSecond, dispatched.SuggestedSpeedMetersPerSecond);
        Assert.AreEqual(suggestion.SuggestedAuthorityMeters, dispatched.SuggestedAuthorityMeters);
    }

    [TestMethod]
    public async Task Dispatch_OccupiedRouteBlock_LimitsAuthority()
    {
        var (service, sender) = CreateService(("Train 1", At(12, 0, 5)));
        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "A3", Occupancy = OccupancyState.Occupied });

        await service.SetSystemTimeAsync(At(12, 0, 5));

        Assert.AreEqual(100.0, MovementSuggestions(sender).Single().SuggestedAuthorityMeters);
    }

    [TestMethod]
    public async Task Dispatch_MaintenanceClosedRouteBlock_LimitsAuthority()
    {
        var (service, sender) = CreateService(("Train 1", At(12, 0, 5)));
        await service.CloseBlockAsync("B7");
        sender.SentMessages.Clear();

        await service.SetSystemTimeAsync(At(12, 0, 5));

        // A1-A5 and B6 are usable: 6 x 50 m.
        Assert.AreEqual(300.0, MovementSuggestions(sender).Single().SuggestedAuthorityMeters);
    }

    [TestMethod]
    public async Task Dispatch_BlockOnOtherBranch_DoesNotLimitAuthority()
    {
        var (service, sender) = CreateService(("Train 1", At(12, 0, 5)));
        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "C11", Occupancy = OccupancyState.Occupied });

        await service.SetSystemTimeAsync(At(12, 0, 5));

        Assert.AreEqual(500.0, MovementSuggestions(sender).Single().SuggestedAuthorityMeters);
    }

    [TestMethod]
    public async Task Dispatch_UnsafeStartBlock_KeepsTrainQueuedWithoutSending()
    {
        var (service, sender) = CreateService(("Train 1", At(12, 0, 5)));
        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "A1", Occupancy = OccupancyState.Occupied });
        var changes = new List<CtcStateChangedEventArgs>();
        service.StateChanged += (_, e) => changes.Add(e);

        await service.SetSystemTimeAsync(At(12, 0, 5));

        Assert.IsEmpty(sender.SentMessages);
        Assert.AreEqual(DispatchQueueStatus.Queued, service.State.DispatchQueue.Single().QueueStatus);
        Assert.IsEmpty(service.State.DispatchedTrains);
        var failure = changes.Single(change => change.Kind == CtcStateChangeKind.DispatchFailed);
        Assert.AreEqual("its start block A1 is occupied or closed for maintenance.", failure.Message);

        // Once the block clears, the next time update releases the train.
        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "A1", Occupancy = OccupancyState.Clear });
        await service.SetSystemTimeAsync(At(12, 0, 6));

        Assert.HasCount(1, MovementRequests(sender));
        Assert.HasCount(1, service.State.DispatchedTrains);
    }

    [TestMethod]
    public async Task LateDispatch_UsesPermittedMaximumAndWarns()
    {
        // Next block (A2) was due at 12:00:09; released at 12:00:10.
        var (service, sender) = CreateService(("Train 1", At(12, 0, 5)));
        var changes = new List<CtcStateChangedEventArgs>();
        service.StateChanged += (_, e) => changes.Add(e);

        await service.SetSystemTimeAsync(At(12, 0, 10));

        Assert.AreEqual(50.0 / 3.6, MovementSuggestions(sender).Single().SuggestedSpeedMetersPerSecond, 1e-9);
        var dispatched = changes.Single(change => change.Kind == CtcStateChangeKind.TrainDispatched);
        Assert.IsNotNull(dispatched.Message);
        Assert.Contains("behind schedule for A2", dispatched.Message);
    }

    [TestMethod]
    public async Task OnTimeDispatch_HasNoWarning()
    {
        var (service, _) = CreateService(("Train 1", At(12, 0, 5)));
        var changes = new List<CtcStateChangedEventArgs>();
        service.StateChanged += (_, e) => changes.Add(e);

        await service.SetSystemTimeAsync(At(12, 0, 5));

        Assert.IsNull(changes.Single(change => change.Kind == CtcStateChangeKind.TrainDispatched).Message);
    }

    [TestMethod]
    public async Task LaterTicks_DoNotDispatchAgain()
    {
        var (service, sender) = CreateService(("Train 1", At(12, 0, 5)));

        await service.SetSystemTimeAsync(At(12, 0, 5));
        await service.SetSystemTimeAsync(At(12, 0, 6));
        await service.SetSystemTimeAsync(At(12, 0, 7));

        Assert.HasCount(1, MovementSuggestions(sender));
        Assert.HasCount(1, MovementRequests(sender));
        Assert.HasCount(1, service.State.DispatchedTrains);
    }

    [TestMethod]
    public async Task ClockJump_DispatchesAllDueTrainsInDepartureOrder()
    {
        // Queued out of order on purpose.
        var (service, sender) = CreateService(
            ("Train 3", At(12, 0, 6)),
            ("Train 1", At(12, 0, 2)),
            ("Train 2", At(12, 0, 4)));
        await service.SetSystemTimeAsync(At(12, 0, 0));

        await service.SetSystemTimeAsync(At(12, 0, 10));

        CollectionAssert.AreEqual(
            new[] { "Train 1", "Train 2", "Train 3" },
            MovementRequests(sender).Select(request => request.TrainId).ToArray());
        Assert.IsEmpty(service.State.DispatchQueue);
        Assert.HasCount(3, service.State.DispatchedTrains);
    }

    [TestMethod]
    public async Task FutureTrain_StaysQueued()
    {
        var (service, _) = CreateService(("Train 1", At(12, 0, 5)), ("Train 2", At(12, 1, 0)));

        await service.SetSystemTimeAsync(At(12, 0, 10));

        Assert.AreEqual("Train 1", service.State.DispatchedTrains.Single().TrainId);
        var queued = service.State.DispatchQueue.Single();
        Assert.AreEqual("Train 2", queued.TrainId);
        Assert.AreEqual(DispatchQueueStatus.Queued, queued.QueueStatus);
    }

    [TestMethod]
    public async Task FailedSend_LeavesTrainQueuedAndReportsFailure()
    {
        var (service, sender) = CreateService(("Train 1", At(12, 0, 5)));
        sender.ExceptionToThrow = new MessageSendException("Track Controller is not connected.");
        var changes = new List<CtcStateChangedEventArgs>();
        service.StateChanged += (_, e) => changes.Add(e);

        await service.SetSystemTimeAsync(At(12, 0, 5));

        var entry = service.State.DispatchQueue.Single();
        Assert.AreEqual(DispatchQueueStatus.Queued, entry.QueueStatus);
        Assert.IsEmpty(service.State.DispatchedTrains);

        var failure = changes.Single(change => change.Kind == CtcStateChangeKind.DispatchFailed);
        Assert.AreEqual("Train 1", failure.TrainId);
        Assert.AreEqual("Track Controller is not connected.", failure.Message);
        Assert.IsFalse(changes.Any(change => change.Kind == CtcStateChangeKind.TrainDispatched));
    }

    [TestMethod]
    public async Task FailedReleaseAfterSuggestion_LeavesTrainQueued()
    {
        var sender = new SelectiveFailingSender("Train 1");
        var service = BlueLine.CreateService(sender);
        service.QueueSchedule([Train("Train 1", At(12, 0, 5))]);

        await service.SetSystemTimeAsync(At(12, 0, 5));

        Assert.AreEqual(1, sender.SuggestionCount);
        Assert.AreEqual(DispatchQueueStatus.Queued, service.State.DispatchQueue.Single().QueueStatus);
        Assert.IsEmpty(service.State.DispatchedTrains);
    }

    [TestMethod]
    public async Task FailedSend_IsRetriedOnNextTimeUpdate()
    {
        var (service, sender) = CreateService(("Train 1", At(12, 0, 5)));
        sender.ExceptionToThrow = new MessageSendException("Track Controller is not connected.");
        await service.SetSystemTimeAsync(At(12, 0, 5));

        sender.ExceptionToThrow = null;
        await service.SetSystemTimeAsync(At(12, 0, 6));
        await service.SetSystemTimeAsync(At(12, 0, 7));

        Assert.HasCount(1, MovementSuggestions(sender));
        Assert.HasCount(1, MovementRequests(sender));
        Assert.HasCount(1, service.State.DispatchedTrains);
        Assert.IsEmpty(service.State.DispatchQueue);
    }

    [TestMethod]
    public async Task FailedSend_DoesNotStopOtherDueTrains()
    {
        var sender = new SelectiveFailingSender("Train 1");
        var service = BlueLine.CreateService(sender);
        service.QueueSchedule([Train("Train 1", At(12, 0, 2)), Train("Train 2", At(12, 0, 4))]);

        await service.SetSystemTimeAsync(At(12, 0, 10));

        Assert.AreEqual("Train 1", service.State.DispatchQueue.Single().TrainId);
        Assert.AreEqual("Train 2", service.State.DispatchedTrains.Single().TrainId);
    }

    [TestMethod]
    public async Task QueueSchedule_DoesNotRequeueDispatchedTrain()
    {
        var (service, sender) = CreateService(("Train 1", At(12, 0, 5)), ("Train 2", At(12, 0, 10)));
        await service.SetSystemTimeAsync(At(12, 0, 5));

        service.QueueSchedule([Train("Train 1", At(12, 0, 5)), Train("Train 2", At(12, 0, 10))]);
        await service.SetSystemTimeAsync(At(12, 0, 6));

        Assert.AreEqual("Train 2", service.State.DispatchQueue.Single().TrainId);
        Assert.AreEqual("Train 1", service.State.DispatchedTrains.Single().TrainId);
        Assert.HasCount(1, MovementRequests(sender));
    }

    [TestMethod]
    public async Task ClockMovingBackward_DoesNotUndoDispatch()
    {
        var (service, sender) = CreateService(("Train 1", At(12, 0, 5)), ("Train 2", At(12, 0, 10)));
        await service.SetSystemTimeAsync(At(12, 0, 5));

        await service.SetSystemTimeAsync(At(11, 59, 0));

        Assert.AreEqual("Train 1", service.State.DispatchedTrains.Single().TrainId);
        Assert.AreEqual("Train 2", service.State.DispatchQueue.Single().TrainId);
        Assert.HasCount(1, MovementRequests(sender));
    }

    [TestMethod]
    public async Task SetSystemTimeAsync_StoresTime()
    {
        var (service, _) = CreateService();

        await service.SetSystemTimeAsync(At(12, 34, 56));

        Assert.AreEqual(At(12, 34, 56), service.State.SystemTime);
    }

    [TestMethod]
    public async Task Dispatch_RaisesSystemTimeThenTrainDispatched()
    {
        var (service, _) = CreateService(("Train 1", At(12, 0, 5)));
        var changes = new List<CtcStateChangedEventArgs>();
        service.StateChanged += (_, e) => changes.Add(e);

        await service.SetSystemTimeAsync(At(12, 0, 5));

        CollectionAssert.AreEqual(
            new[] { CtcStateChangeKind.SystemTime, CtcStateChangeKind.TrainDispatched },
            changes.Select(change => change.Kind).ToArray());
        Assert.AreEqual("Train 1", changes[1].TrainId);
    }

    [TestMethod]
    public async Task OverlappingTimeUpdates_SendOnlyOnce()
    {
        // A second time update (or Queue Schedule) can run while the first is still awaiting
        // its send; the in-flight train must not be picked up or re-queued.
        var sender = new BlockingSender();
        var service = BlueLine.CreateService(sender);
        service.QueueSchedule([Train("Train 1", At(12, 0, 5))]);

        var first = service.SetSystemTimeAsync(At(12, 0, 5));
        Assert.AreEqual(DispatchQueueStatus.Dispatching, service.State.DispatchQueue.Single().QueueStatus);

        await service.SetSystemTimeAsync(At(12, 0, 6));
        service.QueueSchedule([Train("Train 1", At(12, 0, 5))]);
        sender.Release();
        await first;

        Assert.AreEqual(1, sender.Sent.OfType<MovementSuggestionMessage>().Count());
        Assert.AreEqual(1, sender.Sent.OfType<MovementRequestMessage>().Count());
        Assert.IsEmpty(service.State.DispatchQueue);
        Assert.HasCount(1, service.State.DispatchedTrains);
    }

    /// <summary>Fails only the named train's MovementRequest; its MovementSuggestion still succeeds.</summary>
    private sealed class SelectiveFailingSender(string failingTrainId) : IMessageSender
    {
        public int SuggestionCount { get; private set; }

        public Task SendAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
            where TMessage : class
        {
            if (message is MovementSuggestionMessage suggestion && suggestion.TrainId == failingTrainId)
            {
                SuggestionCount++;
            }

            if (message is MovementRequestMessage request && request.TrainId == failingTrainId)
            {
                throw new MessageSendException("Track Controller is not connected.");
            }

            return Task.CompletedTask;
        }
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
            return _release.Task;
        }
    }
}
