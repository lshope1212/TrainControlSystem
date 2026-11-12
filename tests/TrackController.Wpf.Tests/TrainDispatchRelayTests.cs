using Pipe = TrackController.Wpf.Services.WaysidePipeHost.Pipe;
using System.Collections.Concurrent;
using TrackController.Contracts.Messages;
using TrackController.Core.Services;
using TrackController.Runtime;
using TrackController.Wpf.Services;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Wpf.Tests;

/// <summary>
/// The Track Controller as dispatch intermediary (production <see cref="WaysidePipeHost"/> over isolated
/// pipes): it forwards CTC's dispatch to the Track Model, reports each outcome to CTC, relays the Track
/// Model's statuses back, and never lets a dispatch reach the controller runtime (PLC) or grant authority.
/// </summary>
[TestClass]
public sealed class TrainDispatchRelayTests
{
    /// <summary>A runtime that records every message it is given, with blocks A (clear), B (occupied), C (closed).</summary>
    private sealed class RecordingRuntime : IWaysideRuntime
    {
        public RecordingRuntime()
        {
            var original = new TrackControllerService().GetSnapshot();
            var template = original.Blocks[0];
            LatestSnapshot = original with
            {
                IsPaused = false,
                ValidUntilUtc = DateTimeOffset.UtcNow.AddMinutes(5),
                Blocks = [.. new[] { "A", "B", "C" }.Select(id => template with
                {
                    BlockId = id, IsFresh = true, IsFaulted = false, Request = null,
                    Occupancy = id == "B" ? OccupancyState.Occupied : OccupancyState.Clear,
                    Maintenance = id == "C" ? "Closed" : "Open",
                    AuthorizedAuthorityMeters = 0, AuthorizedSpeedMetersPerSecond = 0,
                })],
            };
        }

        public ConcurrentQueue<string> Received { get; } = new();

        public WaysideSnapshotMessage LatestSnapshot { get; }

        public Task<WaysideSnapshotMessage> PollAsync(CancellationToken cancellationToken = default) => Task.FromResult(LatestSnapshot);

        public Task<object> ReceiveAsync(MessageEnvelope message, CancellationToken cancellationToken = default)
        {
            Received.Enqueue(message.MessageType);
            return Task.FromResult<object>(new WaysideAcknowledgementMessage(true, "Accepted", message.MessageType));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _listeners = [];

        public Session(bool trackModelListening = true)
        {
            Pipes = WaysidePipeHost.CreatePipeNames(isolated: true);
            Host = new WaysidePipeHost(Runtime, Pipes);
            _listeners.Add(Listen(Pipes[(int)Pipe.Ctc], ToCtc));
            if (trackModelListening) _listeners.Add(Listen(Pipes[(int)Pipe.TrackModel], ToTrackModel));
            Host.Start();
        }

        public string[] Pipes { get; }

        public RecordingRuntime Runtime { get; } = new();

        public WaysidePipeHost Host { get; }

        public ConcurrentQueue<MessageEnvelope> ToCtc { get; } = new();

        public ConcurrentQueue<MessageEnvelope> ToTrackModel { get; } = new();

        public IEnumerable<TrainDispatchStatusMessage> CtcStatuses => ToCtc.Where(e => e.MessageType == nameof(TrainDispatchStatusMessage)).Select(MessageSerializer.DeserializePayload<TrainDispatchStatusMessage>);

        public Task SendAsync(object message) => NamedPipeTransport.SendAsync(Pipes[(int)Pipe.Input], message);

        private Task Listen(string pipe, ConcurrentQueue<MessageEnvelope> into) =>
            NamedPipeTransport.ListenAsync(pipe, envelope => { into.Enqueue(envelope); return Task.CompletedTask; }, _ => { }, _stop.Token);

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await _stop.CancelAsync();
            await Task.WhenAll(_listeners);
            _stop.Dispose();
        }
    }

    private static TrainDispatchRequestMessage Dispatch(string start, string requestId = "d1", string trainId = "001") => new()
    {
        RequestId = requestId, TrainId = trainId, LineId = "Test", StartBlockId = start, RouteBlockIds = [start, "X"],
    };

    private static async Task UntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out: " + because);
            await Task.Delay(20);
        }
    }

    [TestMethod]
    public async Task Dispatch_IsForwardedToTheTrackModel_ReportedToCtc_AndNeverReachesThePlc()
    {
        await using var session = new Session();

        await session.SendAsync(Dispatch("A"));

        await UntilAsync(() => session.ToTrackModel.Any(e => e.MessageType == nameof(TrainDispatchRequestMessage)), "the dispatch reaches the Track Model.");
        var forwarded = MessageSerializer.DeserializePayload<TrainDispatchRequestMessage>(session.ToTrackModel.First(e => e.MessageType == nameof(TrainDispatchRequestMessage)));
        Assert.AreEqual("d1", forwarded.RequestId);
        CollectionAssert.AreEqual(new[] { "A", "X" }, forwarded.RouteBlockIds.ToArray());

        await UntilAsync(() => session.CtcStatuses.Any(), "CTC learns it was forwarded.");
        var status = session.CtcStatuses.Single();
        Assert.AreEqual(TrainDispatchStage.ForwardedToTrackModel, status.Stage);
        Assert.AreEqual("d1", status.RequestId);
        Assert.AreEqual("Track Controller", status.ReportedBy);

        Assert.IsFalse(session.Runtime.Received.Any(type => type.StartsWith("TrainDispatch", StringComparison.Ordinal)), "Dispatch is relayed at the host; the controller runtime and PLC never see it.");
        Assert.IsFalse(session.ToCtc.Any(e => e.MessageType == nameof(RequestAcknowledgementMessage)), "No movement acknowledgement: nothing was evaluated.");
        Assert.IsTrue(session.Host.LatestSnapshot.Blocks.All(block => block.AuthorizedAuthorityMeters == 0), "Accepting a dispatch grants no authority.");
    }

    [TestMethod]
    [DataRow("B", "not clear", DisplayName = "Occupied start block")]
    [DataRow("C", "closed for maintenance", DisplayName = "Start block closed by the Track Controller")]
    [DataRow("Z", "not controlled by this Track Controller", DisplayName = "Start block another deployment owns")]
    public async Task UnsafeOrUnownedStartBlock_IsRefusedAsRetryable_AndNotForwarded(string start, string reason)
    {
        await using var session = new Session();

        await session.SendAsync(Dispatch(start));

        await UntilAsync(() => session.CtcStatuses.Any(), "CTC gets the refusal.");
        var status = session.CtcStatuses.Single();
        Assert.AreEqual(TrainDispatchStage.Rejected, status.Stage);
        Assert.IsTrue(status.IsRetryable, "The condition can clear; CTC keeps the dispatch and retries.");
        Assert.Contains(reason, status.Detail);
        await Task.Delay(300);
        Assert.IsFalse(session.ToTrackModel.Any(e => e.MessageType == nameof(TrainDispatchRequestMessage)));
    }

    [TestMethod]
    public async Task MalformedDispatch_IsRefusedPermanently()
    {
        await using var session = new Session();

        await session.SendAsync(Dispatch("A", trainId: "011"));

        await UntilAsync(() => session.CtcStatuses.Any(), "CTC gets the refusal.");
        var status = session.CtcStatuses.Single();
        Assert.AreEqual(TrainDispatchStage.Rejected, status.Stage);
        Assert.IsFalse(status.IsRetryable);
        Assert.Contains("dispatchable", status.Detail);
    }

    [TestMethod]
    public async Task UnreachableTrackModel_IsReportedAsARetryableRefusal()
    {
        await using var session = new Session(trackModelListening: false);

        await session.SendAsync(Dispatch("A"));

        await UntilAsync(() => session.CtcStatuses.Any(), "CTC learns the Track Model is unavailable.");
        var status = session.CtcStatuses.Single();
        Assert.AreEqual(TrainDispatchStage.Rejected, status.Stage);
        Assert.IsTrue(status.IsRetryable);
        Assert.Contains("Track Model is not reachable", status.Detail);
    }

    [TestMethod]
    public async Task TrackModelStatuses_AreRelayedToCtcUnchanged()
    {
        await using var session = new Session();
        var placed = new TrainDispatchStatusMessage
        {
            RequestId = "d1", RequestType = nameof(TrainDispatchRequestMessage), TrainId = "001", LineId = "Test", StartBlockId = "A",
            Stage = TrainDispatchStage.Placed, Detail = "initial position report received", ReportedBy = "Track Model", ReportedAtUtc = DateTimeOffset.UtcNow,
        };

        await session.SendAsync(placed);

        await UntilAsync(() => session.CtcStatuses.Any(), "the status reaches CTC.");
        var relayed = session.CtcStatuses.Single();
        Assert.AreEqual(TrainDispatchStage.Placed, relayed.Stage);
        Assert.AreEqual("Track Model", relayed.ReportedBy);
        Assert.AreEqual("initial position report received", relayed.Detail);
        Assert.IsTrue(session.Runtime.Received.All(type => type != nameof(TrainDispatchStatusMessage)));
    }
}
