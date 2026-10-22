using Pipe = TrackController.Wpf.Services.WaysidePipeHost.Pipe;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.IO;
using System.Text;
using TrackController.Contracts.Messages;
using TrackController.Core.Services;
using TrackController.Runtime;
using TrackController.Wpf.Services;
using TrackController.TestUI.Wpf.Services;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Wpf.Tests;

/// <summary>Exercises WaysideTransport behavior without changing live controller state.</summary>
[TestClass]
public sealed class WaysideTransportTests
{
    /// <summary>Keeps both output states visible without notifications per packet, and detects disconnection and recovery.</summary>
    [TestMethod]
    public async Task PublishAsync_ReceiversChange_ReportsCombinedConnectionTransitions()
    {
        var endpoints = WaysidePipeHost.CreatePipeNames(isolated: true);
        await using var host = new WaysidePipeHost(new TrackControllerService(), endpoints);
        var states = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<Exception>();
        host.StatusChanged += states.Enqueue;
        host.Start();
        await UntilAsync(() => states.LastOrDefault() == "CTC: unavailable · Track Model: unavailable");

        using var trackStop = new CancellationTokenSource();
        using var ctcStop = new CancellationTokenSource();
        using var recoveryStop = new CancellationTokenSource();
        long snapshots = 0;
        var trackListener = NamedPipeTransport.ListenAsync(endpoints[(int)Pipe.TrackModel], _ =>
        {
            Interlocked.Increment(ref snapshots);
            return Task.CompletedTask;
        }, errors.Enqueue, trackStop.Token);
        Task ctcListener = Task.CompletedTask;
        Task recoveryListener = Task.CompletedTask;
        try
        {
            await UntilAsync(() => states.LastOrDefault() == "CTC: unavailable · Track Model: connected");
            ctcListener = NamedPipeTransport.ListenAsync(endpoints[(int)Pipe.Ctc], _ => Task.CompletedTask, errors.Enqueue, ctcStop.Token);
            await UntilAsync(() => states.LastOrDefault() == "CTC: connected · Track Model: connected");
            int notifications = states.Count;
            long previousSnapshots = Interlocked.Read(ref snapshots);
            await UntilAsync(() => Interlocked.Read(ref snapshots) >= previousSnapshots + 3);
            Assert.HasCount(notifications, states, "Normal publication must not replace the footer every cycle.");

            await ctcStop.CancelAsync();
            await ctcListener;
            await UntilAsync(() => states.LastOrDefault() == "CTC: unavailable · Track Model: connected");
            recoveryListener = NamedPipeTransport.ListenAsync(endpoints[(int)Pipe.Ctc], _ => Task.CompletedTask, errors.Enqueue, recoveryStop.Token);
            await UntilAsync(() => states.LastOrDefault() == "CTC: connected · Track Model: connected");
            Assert.IsEmpty(errors);
        }
        finally
        {
            await trackStop.CancelAsync();
            await ctcStop.CancelAsync();
            await recoveryStop.CancelAsync();
            await Task.WhenAll(trackListener, ctcListener, recoveryListener);
        }
    }

    private sealed class NetworkFixture : IAsyncDisposable
    {
        public string[] Endpoints { get; } = WaysidePipeHost.CreatePipeNames(isolated: true);

        public WaysideTestConnection Peers { get; }

        private WaysidePipeHost? _host;

        /// <summary>Creates an isolated production pipe host and simulated external peers.</summary>
        public NetworkFixture()
        {
            Peers = new(Endpoints[(int)Pipe.Input], Endpoints[(int)Pipe.Ctc], Endpoints[(int)Pipe.TrackModel]);
            Peers.Start();
            _host = new WaysidePipeHost(new TrackControllerService(), Endpoints);
            _host.Start();
        }

        /// <summary>Stops the test receiver so transport-failure behavior can be observed.</summary>
        public async Task StopControllerAsync()
        {
            if (_host is not null)
            {
                await _host.DisposeAsync();
                _host = null;
            }
        }

        /// <summary>Waits for real published outputs after initializing the test controller.</summary>
        public async Task ReadyAsync()
        {
            await Peers.SendAsync(MessageSerializer.CreateEnvelope("SetControllerMode", new { hardwareMode = false }));
            await FeedbackAsync();
            await Peers.SendAsync(MessageSerializer.CreateEnvelope("SetSimulation", new { paused = false, speedMultiplier = 1 }));
            await SnapshotAsync(snapshot => !snapshot.IsPaused && snapshot.Blocks.All(blockStatus => blockStatus.IsFresh));
        }

        /// <summary>Sends fresh sample measurements through the actual named-pipe input.</summary>
        public Task FeedbackAsync(Action<List<TrackModelBlockStateMessage>>? change = null)
        {
            var blocks = Enumerable.Range(1, 12).Select(index => new TrackModelBlockStateMessage { BlockId = index.ToString(), ObservedAtUtc = DateTimeOffset.UtcNow, Occupancy = index == 5 ? OccupancyState.Occupied : OccupancyState.Clear, Signal = SignalState.Green, Switch = SwitchPosition.Normal, Crossing = CrossingState.Closed, IsReservedByCtc = false }).ToList();
            change?.Invoke(blocks);
            return Peers.SendAsync(blocks);
        }

        /// <summary>Waits for a published snapshot satisfying the test condition.</summary>
        public async Task<WaysideSnapshotMessage> SnapshotAsync(Func<WaysideSnapshotMessage, bool> predicate)
        {
            await UntilAsync(() => Peers.LatestSnapshot is { } snapshot && predicate(snapshot));
            return Peers.LatestSnapshot!;
        }

        /// <summary>Stops owned background operations and releases connections before returning.</summary>
        public async ValueTask DisposeAsync()
        {
            await StopControllerAsync();
            await Peers.DisposeAsync();
        }
    }

    private static MovementSuggestionMessage Movement() => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        TrainId = "101",
        TargetBlockId = "5",
        PathBlockIds = ["6", "7", "8"],
        SuggestedSpeedMetersPerSecond = 11.176,
        SuggestedAuthorityMeters = 365.76
    };

    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate())
        {
            await Task.Delay(15, deadline.Token);
        }
    }

    private static IEnumerable<WaysideAcknowledgementMessage> Results(WaysideTestConnection peers) => peers.RecentMessages.Where(envelope => envelope.MessageType == nameof(WaysideAcknowledgementMessage)).Select(MessageSerializer.DeserializePayload<WaysideAcknowledgementMessage>);

    /// <summary>Verifies that legacy train only request is rejected without guessing occupancy.</summary>
    [TestMethod]
    public async Task Receive_TrainOnlyMovement_RejectsWithoutGuessingPosition()
    {
        await using var runtime = new LocalWaysideRuntime(new TrackControllerService());
        var reply = (WaysideAcknowledgementMessage)await runtime.ReceiveAsync(MessageSerializer.Deserialize(MessageSerializer.Serialize(new MovementSuggestionMessage { TrainId = "101" })));
        Assert.IsFalse(reply.IsAccepted);
        StringAssert.Contains(reply.Detail, "target block");
    }

    /// <summary>Rejects the old position-based message instead of silently changing the meaning of its authority.</summary>
    [TestMethod]
    public async Task Receive_LegacyFrontOffset_RejectsBeforeGrantingAuthority()
    {
        await using var runtime = new LocalWaysideRuntime(new TrackControllerService());
        var payload = System.Text.Json.Nodes.JsonNode.Parse(MessageSerializer.Serialize(Movement()))!;
        payload["payload"]!["frontOffsetMeters"] = 60;
        var result = (WaysideAcknowledgementMessage)await runtime.ReceiveAsync(MessageSerializer.Deserialize(payload.ToJsonString()));
        Assert.IsFalse(result.IsAccepted);
        StringAssert.Contains(result.Detail, "frontOffsetMeters");
    }

    /// <summary>Malformed envelopes fail at the transport parser; invalid payloads are rejected by the runtime.</summary>
    [TestMethod]
    public async Task Receive_InvalidEnvelopeOrEnums_ReturnsRejection()
    {
        await using var runtime = new LocalWaysideRuntime(new TrackControllerService());
        foreach (string json in new[]
        {
            "{bad",
            "{}",
            "{\"messageType\":\"MovementSuggestionMessage\",\"payload\":null}"
        }

        )
        {
            Assert.ThrowsExactly<System.Text.Json.JsonException>(() => MessageSerializer.Deserialize(json));
        }

        foreach (string json in new[]
        {
            "{\"messageType\":\"Unknown\",\"payload\":{}}",
            "{\"messageType\":\"MaintenanceRequestMessage\",\"payload\":{\"blockId\":\"5\",\"requestedState\":99}}"
        })
        {
            Assert.IsFalse(((WaysideAcknowledgementMessage)await runtime.ReceiveAsync(MessageSerializer.Deserialize(json))).IsAccepted);
        }
    }

    /// <summary>Verifies that test client uses actual input and both output pipes.</summary>
    [TestMethod]
    public async Task SendAsync_TestInput_TraversesInputAndBothOutputPipes()
    {
        await using var fixture = new NetworkFixture();
        await fixture.ReadyAsync();
        var request = Movement();
        await fixture.Peers.SendAsync(request);
        var output = await fixture.SnapshotAsync(snapshot => snapshot.Authorizations.Any(authorization => authorization.Request!.RequestId == request.RequestId && authorization.AuthorizedAuthorityMeters > 0));
        Assert.AreEqual(365.76, output.Authorizations.Single().AuthorizedAuthorityMeters);
        Assert.AreEqual(MovementSuggestionMessage.BlockEntryReference, output.Authorizations.Single().Request!.AuthorityReference);
        Assert.AreEqual(11.176, output.Authorizations.Single().AuthorizedSpeedMetersPerSecond);
        await UntilAsync(() => fixture.Peers.RecentMessages.Any(envelope => envelope.MessageType == nameof(TrackModelCommandMessage) && MessageSerializer.DeserializePayload<TrackModelCommandMessage>(envelope).AuthorityMeters == 365.76));
        Assert.IsGreaterThan(0L, fixture.Peers.CtcReceivedCount);
        Assert.IsGreaterThan(0L, fixture.Peers.TrackModelReceivedCount);
    }

    /// <summary>Verifies that shared module sender and test sender reach same receiver.</summary>
    [TestMethod]
    public async Task SendAsync_ModuleAndTestSenders_ReachSameReceiver()
    {
        await using var fixture = new NetworkFixture();
        // This is the exact Common transport now called by CTC's production sender.
        await NamedPipeTransport.SendAsync(fixture.Endpoints[(int)Pipe.Input], new MaintenanceRequestMessage { BlockId = "12", RequestedState = MaintenanceState.Closed });
        await fixture.SnapshotAsync(snapshot => snapshot.Blocks.Single(blockStatus => blockStatus.BlockId == "12").Maintenance == "Pending closure");
        await fixture.Peers.SendAsync(new MaintenanceRequestMessage { BlockId = "12", RequestedState = MaintenanceState.Open });
        await fixture.SnapshotAsync(snapshot => snapshot.Blocks.Single(blockStatus => blockStatus.BlockId == "12").Maintenance == "Open");
    }

    [TestMethod]
    public async Task PublishAsync_MaintenanceChanges_ReportsConfirmedClosureAndReopeningToCtc()
    {
        await using var fixture = new NetworkFixture();
        await fixture.ReadyAsync();
        MaintenanceState? ReportedMaintenance() => fixture.Peers.RecentMessages
            .Where(envelope => envelope.MessageType == nameof(BlockStatusMessage))
            .Select(MessageSerializer.DeserializePayload<BlockStatusMessage>)
            .LastOrDefault(message => message.BlockId == "12")?.Maintenance;

        await fixture.Peers.SendAsync(new MaintenanceRequestMessage { BlockId = "12", RequestedState = MaintenanceState.Closed });
        await fixture.SnapshotAsync(snapshot => snapshot.Blocks.Single(block => block.BlockId == "12").Maintenance == "Closed");
        await UntilAsync(() => ReportedMaintenance() == MaintenanceState.Closed);

        await fixture.Peers.SendAsync(new MaintenanceRequestMessage { BlockId = "12", RequestedState = MaintenanceState.Open });
        await fixture.SnapshotAsync(snapshot => snapshot.Blocks.Single(block => block.BlockId == "12").Maintenance == "Open");
        await UntilAsync(() => ReportedMaintenance() == MaintenanceState.Open);
    }

    /// <summary>Verifies that typed input is serialized before controller observes it.</summary>
    [TestMethod]
    public async Task SendAsync_TypedInput_SerializesBeforeDelivery()
    {
        await using var fixture = new NetworkFixture();
        await fixture.ReadyAsync();
        var request = Movement();
        await fixture.Peers.SendAsync(request);
        request.PathBlockIds.Clear();
        request.SuggestedAuthorityMeters = 99999;
        var output = await fixture.SnapshotAsync(snapshot => snapshot.Authorizations.Count == 1);
        Assert.HasCount(3, output.Authorizations[0].Request!.PathBlockIds);
        Assert.AreEqual(365.76, output.Authorizations[0].Request!.SuggestedAuthorityMeters);
    }

    /// <summary>Verifies that malformed connection does not kill shared receiver.</summary>
    [TestMethod]
    public async Task ListenAsync_MalformedConnection_ContinuesReceiving()
    {
        await using var fixture = new NetworkFixture();
        await using (var client = new NamedPipeClientStream(".", fixture.Endpoints[(int)Pipe.Input], PipeDirection.Out, PipeOptions.Asynchronous))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(deadline.Token);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false));
            await writer.WriteLineAsync("broken JSON");
            await writer.FlushAsync();
        }

        await fixture.Peers.SendAsync(new MaintenanceRequestMessage { BlockId = "3", RequestedState = MaintenanceState.Closed });
        await fixture.SnapshotAsync(snapshot => snapshot.Blocks.Single(blockStatus => blockStatus.BlockId == "3").Maintenance == "Pending closure");
    }

    /// <summary>Verifies that rejected input returns real published application result.</summary>
    [TestMethod]
    public async Task Receive_RejectedInput_PublishesApplicationResult()
    {
        await using var fixture = new NetworkFixture();
        var request = Movement();
        request.PathBlockIds = ["foreign"];
        await fixture.Peers.SendAsync(request);
        await UntilAsync(() => Results(fixture.Peers).Any(acknowledgement => !acknowledgement.IsAccepted && acknowledgement.MessageType == nameof(MovementSuggestionMessage)));
        var snapshot = await fixture.SnapshotAsync(snapshot => snapshot.Authorizations.Count == 0);
        Assert.IsEmpty(snapshot.Authorizations);
    }

    /// <summary>Verifies that hardware plc and manual commands use same wire interface.</summary>
    [TestMethod]
    public async Task SendAsync_PlcAndManualCommands_UsesProductionInterface()
    {
        await using var fixture = new NetworkFixture();
        await fixture.Peers.SendAsync(MessageSerializer.CreateEnvelope("LoadPlc", new { name = "wire.plc", source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PiSamples", "wayside.plc")) }));
        await fixture.SnapshotAsync(snapshot => snapshot.PlcName == "wire.plc" && snapshot.IsPaused);
        await fixture.FeedbackAsync();
        await fixture.Peers.SendAsync(MessageSerializer.CreateEnvelope("SetMaintenanceMode", new { enabled = true }));
        await fixture.Peers.SendAsync(MessageSerializer.CreateEnvelope("ApplyManual", new { blockId = "4", signal = SignalState.Red }));
        await UntilAsync(() => Results(fixture.Peers).Any(acknowledgement => acknowledgement.IsAccepted && acknowledgement.MessageType == "ApplyManual"));
        await fixture.SnapshotAsync(snapshot => snapshot.IsMaintenanceMode && snapshot.Blocks.Single(blockStatus => blockStatus.BlockId == "4").CommandedSignal == SignalState.Red);
    }

    /// <summary>Verifies that repeating feedback and movement use transport on every send.</summary>
    [TestMethod]
    public async Task SendAsync_RepeatedInputs_UsesTransportEveryTime()
    {
        await using var fixture = new NetworkFixture();
        await fixture.ReadyAsync();
        var request = Movement();
        for (int i = 0; i < 3; i++)
        {
            await fixture.FeedbackAsync();
            await fixture.Peers.SendAsync(request);
        }

        Assert.AreEqual(9L, fixture.Peers.SentCount);
        await fixture.SnapshotAsync(snapshot => snapshot.Authorizations.Any(authorization => authorization.Request!.RequestId == request.RequestId && authorization.AuthorizedAuthorityMeters > 0));
    }

    /// <summary>Verifies that lost controller cannot be hidden by direct call fallback.</summary>
    [TestMethod]
    public async Task SendAsync_StoppedController_FailsWithoutDirectFallback()
    {
        await using var fixture = new NetworkFixture();
        await fixture.ReadyAsync();
        await fixture.StopControllerAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Peers.SendAsync(Movement(), deadline.Token));
        await UntilAsync(() => !fixture.Peers.IsSnapshotCurrent);
    }

    /// <summary>Verifies that no output listener means no observed state even when input was delivered.</summary>
    [TestMethod]
    public async Task SendAsync_MissingOutputListener_DoesNotExposePrivateState()
    {
        var endpoints = WaysidePipeHost.CreatePipeNames(isolated: true);
        await using var host = new WaysidePipeHost(new TrackControllerService(), endpoints);
        host.Start();
        await using var peers = new WaysideTestConnection(endpoints[(int)Pipe.Input], endpoints[(int)Pipe.Ctc], endpoints[(int)Pipe.TrackModel]); // Deliberately do not start output listeners.
        await peers.SendAsync(new MaintenanceRequestMessage { BlockId = "12", RequestedState = MaintenanceState.Closed });
        Assert.IsNull(peers.LatestSnapshot);
        Assert.IsFalse(peers.IsSnapshotCurrent);
    }

    /// <summary>Verifies that isolated test does not mutate another running controller.</summary>
    [TestMethod]
    public async Task SendAsync_IsolatedTest_LeavesOtherControllerUnchanged()
    {
        await using var live = new NetworkFixture();
        await using var test = new NetworkFixture();
        await test.Peers.SendAsync(new MaintenanceRequestMessage { BlockId = "12", RequestedState = MaintenanceState.Closed });
        await test.SnapshotAsync(snapshot => snapshot.Blocks.Single(blockStatus => blockStatus.BlockId == "12").Maintenance == "Pending closure");
        var untouched = await live.SnapshotAsync(snapshot => snapshot.Blocks.Count == 12);
        Assert.AreEqual("Open", untouched.Blocks.Single(blockStatus => blockStatus.BlockId == "12").Maintenance);
    }

    /// <summary>Verifies cancellation stops Track Model movement while CTC receives measured block feedback.</summary>
    [TestMethod]
    public async Task PublishAsync_CancelledMovement_StopsTrackModelCommand()
    {
        await using var fixture = new NetworkFixture();
        await fixture.ReadyAsync();
        var request = Movement();
        await fixture.Peers.SendAsync(request);
        await UntilAsync(() => fixture.Peers.RecentMessages.Any(envelope => envelope.MessageType == nameof(TrackModelCommandMessage) && MessageSerializer.DeserializePayload<TrackModelCommandMessage>(envelope).AuthorityMeters > 0));
        request.IsActive = false;
        await fixture.Peers.SendAsync(request);
        await fixture.SnapshotAsync(snapshot => snapshot.Authorizations.Count == 0);
        await UntilAsync(() => fixture.Peers.RecentMessages.Where(envelope => envelope.MessageType == nameof(TrackModelCommandMessage))
            .Select(MessageSerializer.DeserializePayload<TrackModelCommandMessage>).LastOrDefault(command => command.BlockId == request.TargetBlockId) is { AuthorityMeters: 0, CommandedSpeedMetersPerSecond: 0 });
        var block = fixture.Peers.RecentMessages.Where(envelope => envelope.MessageType == nameof(BlockStatusMessage)).Select(MessageSerializer.DeserializePayload<BlockStatusMessage>).Last(blockStatus => blockStatus.BlockId == "6");
        Assert.AreEqual(SwitchPosition.Normal, block.Switch);
    }

    /// <summary>Verifies that snapshot requests use the diagnostic pipe while module outputs continue separately.</summary>
    [TestMethod]
    public async Task Receive_SnapshotRequest_UsesDiagnosticOutputPipe()
    {
        await using var fixture = new NetworkFixture();
        await fixture.Peers.SendAsync(MessageSerializer.CreateEnvelope("GetSnapshot", new { }));
        await fixture.SnapshotAsync(snapshot => snapshot.Blocks.Count == 12);
        Assert.IsGreaterThan(0L, fixture.Peers.TrackModelReceivedCount);
    }
}
