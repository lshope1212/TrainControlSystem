using Pipe = TrackController.Wpf.Services.WaysidePipeHost.Pipe;
using System.Collections.Concurrent;
using TrackController.Contracts.Messages;
using TrackController.Core.Services;
using TrackController.Runtime;
using TrackController.Wpf.Services;
using TrackModel.Core.Models;
using TrackModel.Core.Services;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Wpf.Tests;

[TestClass]
public sealed class WaysidePublicationTests
{
    private sealed class SnapshotRuntime(WaysideSnapshotMessage snapshot) : IWaysideRuntime
    {
        private WaysideSnapshotMessage _snapshot = snapshot;
        public WaysideSnapshotMessage LatestSnapshot { get => Volatile.Read(ref _snapshot); set => Volatile.Write(ref _snapshot, value); }
        public Task<WaysideSnapshotMessage> PollAsync(CancellationToken cancellationToken = default) => Task.FromResult(LatestSnapshot);
        public Task<object> ReceiveAsync(MessageEnvelope message, CancellationToken cancellationToken = default) => Task.FromResult<object>(
            message.MessageType == "GetSnapshot" ? LatestSnapshot : new WaysideAcknowledgementMessage(true, "Accepted", message.MessageType));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static WaysideSnapshotMessage Snapshot()
    {
        var original = new TrackControllerService().GetSnapshot();
        var template = original.Blocks[0];
        var blocks = new[] { "A", "B", "C" }.Select(id => template with
        {
            BlockId = id, LengthMeters = 100, SpeedLimitMetersPerSecond = 20,
            HasSignal = id != "C", HasSwitch = id == "B", HasCrossing = id == "A",
            Occupancy = id == "A" ? OccupancyState.Occupied : OccupancyState.Clear,
            Maintenance = "Open", IsFresh = true, IsFaulted = false,
            CommandedSignal = id == "A" ? SignalState.Green : SignalState.Red, ReportedSignal = SignalState.Yellow,
            CommandedSwitch = id == "B" ? SwitchPosition.Reverse : SwitchPosition.Unknown, ReportedSwitch = SwitchPosition.Normal,
            CommandedCrossing = CrossingState.Closed, ReportedCrossing = CrossingState.Open,
            Request = id == "A" ? new MovementSuggestionMessage { RequestId = "request", TrainId = "train", TargetBlockId = "A", PathBlockIds = ["B", "C"], SuggestedSpeedMetersPerSecond = 19, SuggestedAuthorityMeters = 300 } : null,
            AuthorizedSpeedMetersPerSecond = id == "A" ? 7 : 0, AuthorizedAuthorityMeters = id == "A" ? 250 : 0,
            MovementStatus = id == "A" ? "Approved" : "", MovementReason = id == "A" ? "Test output" : ""
        }).ToArray();
        return original with
        {
            IsPaused = false, IsMaintenanceMode = false, ValidUntilUtc = DateTimeOffset.UtcNow.AddMinutes(1), Blocks = blocks
        };
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(15, timeout.Token);
    }

    [TestMethod]
    public async Task PublishAsync_BothConversions_TrackModelAcceptsCommandsAndDiagnosticsStaySeparate()
    {
        var snapshot = Snapshot();
        var track = new TrackService();
        track.LoadLayout(new TrackLayout
        {
            Name = "Publication test",
            Blocks = snapshot.Blocks.Select(b => new TrackBlock
            {
                Id = b.BlockId, LineId = "test", LengthMeters = b.LengthMeters,
                HasSignal = b.HasSignal, HasSwitch = b.HasSwitch, HasCrossing = b.HasCrossing,
                ConnectedBlockIds = b.HasSwitch ? ["A", "C"] : ["B"],
                NormalNextBlockId = b.HasSwitch ? "A" : "", ReverseNextBlockId = b.HasSwitch ? "C" : ""
            }).ToList()
        });
        var runtime = new SnapshotRuntime(snapshot);
        var endpoints = WaysidePipeHost.CreatePipeNames(isolated: true);
        using var stop = new CancellationTokenSource();
        var errors = new ConcurrentQueue<Exception>();
        var ctc = new ConcurrentQueue<MessageEnvelope>();
        var monitor = new ConcurrentQueue<MessageEnvelope>();
        var trackMessages = new ConcurrentQueue<MessageEnvelope>();
        var latest = new ConcurrentDictionary<string, TrackModelCommandMessage>();
        var listeners = new[]
        {
            NamedPipeTransport.ListenAsync(endpoints[(int)Pipe.Ctc], e => { ctc.Enqueue(e); return Task.CompletedTask; }, errors.Enqueue, stop.Token),
            NamedPipeTransport.ListenAsync(endpoints[(int)Pipe.TrackModel], e =>
            {
                trackMessages.Enqueue(e);
                Assert.AreEqual(nameof(TrackModelCommandMessage), e.MessageType);
                var command = MessageSerializer.DeserializePayload<TrackModelCommandMessage>(e);
                track.ApplyCommand(command); // The unmodified production Track Model service validates and applies the payload.
                latest[command.BlockId] = command;
                return Task.CompletedTask;
            }, errors.Enqueue, stop.Token),
            NamedPipeTransport.ListenAsync(endpoints[(int)Pipe.Monitor], e => { monitor.Enqueue(e); return Task.CompletedTask; }, errors.Enqueue, stop.Token)
        };
        try
        {
            await using var host = new WaysidePipeHost(runtime, endpoints);
            host.Start();
            await UntilAsync(() => latest.Count == 3 && ctc.Count >= 3 && !monitor.IsEmpty);
            Assert.AreEqual(7d, track.FindBlock("A")!.CommandedSpeedMetersPerSecond);
            Assert.AreEqual(250d, track.FindBlock("A")!.AuthorityMeters);
            Assert.AreEqual(0d, track.FindBlock("B")!.AuthorityMeters, "Route blocks must not inherit the target block's fixed authority limit.");
            Assert.AreEqual(SwitchPosition.Reverse, track.FindBlock("B")!.Switch);
            Assert.AreEqual(CrossingState.Closed, track.FindBlock("A")!.Crossing);
            var report = ctc.Where(e => e.MessageType == nameof(BlockStatusMessage)).Select(MessageSerializer.DeserializePayload<BlockStatusMessage>).First(b => b.BlockId == "B");
            Assert.AreEqual(SwitchPosition.Normal, report.Switch, "CTC receives measured state, not the switch command.");
            Assert.AreEqual(SignalState.Yellow, report.Signal);

            await NamedPipeTransport.SendAsync(endpoints[(int)Pipe.Input], MessageSerializer.CreateEnvelope("GetSnapshot", new { }));
            await NamedPipeTransport.SendAsync(endpoints[(int)Pipe.Input], MessageSerializer.CreateEnvelope("SetSimulation", new { paused = true, speedMultiplier = 1 }));
            await UntilAsync(() => monitor.Any(e => e.MessageType == nameof(WaysideAcknowledgementMessage)));
            Assert.IsTrue(trackMessages.All(e => e.MessageType == nameof(TrackModelCommandMessage)));
            Assert.IsTrue(ctc.All(e => e.MessageType == nameof(BlockStatusMessage)), "The CTC receives only its supported block-status contract.");
            var reportedSnapshot = MessageSerializer.DeserializePayload<WaysideSnapshotMessage>(monitor.Last(e => e.MessageType == nameof(WaysideSnapshotMessage)));
            var movement = reportedSnapshot.Authorizations.Single();
            Assert.AreEqual("request", movement.Request!.RequestId);
            Assert.AreEqual(250d, movement.AuthorizedAuthorityMeters);
            Assert.AreEqual("Approved", movement.MovementStatus);
            Assert.IsFalse(MessageSerializer.CreateEnvelope(nameof(WaysideSnapshotMessage), reportedSnapshot).Payload.TryGetProperty("authorizations", out _), "Results are serialized only on the target blocks.");

            runtime.LatestSnapshot = snapshot with { Blocks = snapshot.Blocks.Select(b => b with { Request = null }).ToArray() };
            await UntilAsync(() => latest["A"].AuthorityMeters == 0 && latest["A"].CommandedSpeedMetersPerSecond == 0);

            runtime.LatestSnapshot = snapshot;
            await UntilAsync(() => latest["A"].AuthorityMeters == 250);
            runtime.LatestSnapshot = snapshot.CreateStopSnapshot("Pi disconnected");
            await UntilAsync(() => latest["A"].AuthorityMeters == 0 && latest["A"].Signal == SignalState.Red && latest["B"].Signal == SignalState.Red);
            Assert.AreEqual(SwitchPosition.Reverse, latest["B"].Switch, "Loss of runtime state must not invent a switch throw.");
            Assert.AreEqual(CrossingState.Closed, latest["A"].Crossing);
            Assert.IsEmpty(errors);
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(listeners);
        }
    }

    [TestMethod]
    public async Task PublishAsync_DiagnosticListenerStartsLate_DeliversQueuedReceiptsWithSnapshots()
    {
        var endpoints = WaysidePipeHost.CreatePipeNames(isolated: true);
        using var stop = new CancellationTokenSource();
        var errors = new ConcurrentQueue<Exception>();
        var messages = new ConcurrentQueue<MessageEnvelope>();
        await using var host = new WaysidePipeHost(new SnapshotRuntime(Snapshot()), endpoints);
        host.Start();
        for (int i = 0; i < 12; i++) await NamedPipeTransport.SendAsync(endpoints[(int)Pipe.Input], MessageSerializer.CreateEnvelope("SetSimulation", new { paused = true, speedMultiplier = 1 }));
        var listener = NamedPipeTransport.ListenAsync(endpoints[(int)Pipe.Monitor], e =>
        {
            messages.Enqueue(e);
            return Task.CompletedTask;
        }, errors.Enqueue, stop.Token);
        try
        {
            await UntilAsync(() => messages.Count(e => e.MessageType == nameof(WaysideAcknowledgementMessage)) == 12
                && messages.Any(e => e.MessageType == nameof(WaysideSnapshotMessage)));
            Assert.IsEmpty(errors);
        }
        finally
        {
            await stop.CancelAsync();
            await listener;
        }
    }

    [TestMethod]
    [DataRow("paused")]
    [DataRow("expired")]
    [DataRow("faulted")]
    [DataRow("wrong-target")]
    [DataRow("unknown-switch")]
    public async Task PublishAsync_UnsafeOrMismatchedMovement_SendsZeroMovement(string condition)
    {
        var snapshot = Snapshot();
        snapshot = condition switch
        {
            "paused" => snapshot with { IsPaused = true },
            "expired" => snapshot with { ValidUntilUtc = DateTimeOffset.UtcNow.AddSeconds(-1) },
            "faulted" => snapshot with { Blocks = snapshot.Blocks.Select(b => b with { IsFaulted = true }).ToArray() },
            "unknown-switch" => snapshot with { Blocks = snapshot.Blocks.Select(b => b.BlockId == "A" ? b with
                { HasSwitch = true, CommandedSwitch = SwitchPosition.Unknown, ReportedSwitch = SwitchPosition.Unknown } : b).ToArray() },
            _ => snapshot with { Blocks = snapshot.Blocks.Select(b => b.Request is null ? b : b with { Request = new MovementSuggestionMessage { TargetBlockId = "B" } }).ToArray() }
        };
        var endpoints = WaysidePipeHost.CreatePipeNames(isolated: true);
        var received = new ConcurrentDictionary<string, TrackModelCommandMessage>();
        var errors = new ConcurrentQueue<Exception>();
        using var stop = new CancellationTokenSource();
        var listener = NamedPipeTransport.ListenAsync(endpoints[(int)Pipe.TrackModel], e =>
        {
            var command = MessageSerializer.DeserializePayload<TrackModelCommandMessage>(e);
            received[command.BlockId] = command;
            return Task.CompletedTask;
        }, errors.Enqueue, stop.Token);
        try
        {
            await using var host = new WaysidePipeHost(new SnapshotRuntime(snapshot), endpoints);
            host.Start();
            await UntilAsync(() => received.Count == (condition == "unknown-switch" ? 2 : 3));
            if (condition == "unknown-switch") Assert.IsFalse(received.ContainsKey("A"), "Do not invent a switch position to satisfy the receiver's contract.");
            Assert.IsTrue(received.Values.All(c => c.AuthorityMeters == 0 && c.CommandedSpeedMetersPerSecond == 0));
            Assert.IsEmpty(errors);
        }
        finally
        {
            await stop.CancelAsync();
            await listener;
        }
    }
}
