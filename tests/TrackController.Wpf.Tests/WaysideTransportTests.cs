using System.IO.Pipes;
using System.IO;
using System.Text;
using TrackController.Contracts.Messages;
using TrackController.Core.Services;
using TrackController.Runtime;
using TrackController.Wpf.Services;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Wpf.Tests;

/// <summary>Exercises WaysideTransport behavior without changing live controller state.</summary>
[TestClass]
public sealed class WaysideTransportTests
{
    private sealed class NetworkFixture : IAsyncDisposable
    {
        public WaysideEndpoints Endpoints { get; } = WaysideEndpoints.CreateIsolated();
        public WaysideTestConnection Peers
        {
            get;
        }

        private WaysidePipeHost? _host;
        /// <summary>Creates an isolated production pipe host and simulated external peers.</summary>
        public NetworkFixture()
        {
            Peers = new(Endpoints);
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
            await Peers.SendAsync(new WaysideControllerModeMessage { IsHardwareMode = false });
            await FeedbackAsync();
            await Peers.SendAsync(new WaysideSimulationMessage { IsPaused = false });
            await SnapshotAsync(snapshot => !snapshot.IsPaused && snapshot.Blocks.All(blockStatus => blockStatus.IsFresh));
        }

        /// <summary>Sends fresh sample measurements through the actual named-pipe input.</summary>
        public Task FeedbackAsync(Action<List<WaysideBlockFeedback>>? change = null)
        {
            var blocks = Enumerable.Range(1, 12).Select(index => new WaysideBlockFeedback { BlockId = index.ToString(), ObservedAtUtc = DateTimeOffset.UtcNow, Occupancy = index == 5 ? OccupancyState.Occupied : OccupancyState.Clear, Signal = SignalState.Green, Switch = SwitchPosition.Normal, Crossing = CrossingState.Closed, IsReservedByCtc = false }).ToList();
            change?.Invoke(blocks);
            return Peers.SendAsync(new WaysideFeedbackMessage { Blocks = blocks });
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

    private static WaysideMovementRequestMessage Movement() => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        TrainId = "101",
        TargetBlockId = "5",
        FrontOffsetMeters = 0,
        PathBlockIds = ["6", "7", "8"],
        SuggestedSpeedMetersPerSecond = 11.176,
        SuggestedAuthorityMeters = 365.76
    };
    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate())
            await Task.Delay(15, deadline.Token);
    }

    private static IEnumerable<WaysideAcknowledgementMessage> Results(WaysideTestConnection peers) => peers.RecentMessages.Where(envelope => envelope.MessageType == nameof(WaysideAcknowledgementMessage)).Select(MessageSerializer.DeserializePayload<WaysideAcknowledgementMessage>);
    /// <summary>Verifies that legacy train only request is rejected without guessing occupancy.</summary>
    [TestMethod]
    public void Receive_TrainOnlyMovement_RejectsWithoutGuessingPosition()
    {
        var router = new WaysideMessageRouter(new TrackControllerService());
        var reply = (WaysideAcknowledgementMessage)router.Receive(MessageSerializer.Serialize(new MovementSuggestionMessage { TrainId = "101" }));
        Assert.IsFalse(reply.IsAccepted);
        StringAssert.Contains(reply.Detail, "target block");
    }

    /// <summary>Verifies that invalid json enums nulls and unknown types return rejections.</summary>
    [TestMethod]
    public void Receive_InvalidEnvelopeOrEnums_ReturnsRejection()
    {
        var router = new WaysideMessageRouter(new TrackControllerService());
        foreach (string json in new[]
        {
            "{bad",
            "{}",
            "{\"messageType\":\"Unknown\",\"payload\":{}}",
            "{\"messageType\":\"WaysideMovementRequestMessage\",\"payload\":null}",
            "{\"messageType\":\"MaintenanceRequestMessage\",\"payload\":{\"blockId\":\"5\",\"requestedState\":99}}"
        }

        )
            Assert.IsFalse(((WaysideAcknowledgementMessage)router.Receive(json)).IsAccepted);
    }

    /// <summary>Verifies that test client uses actual input and both output pipes.</summary>
    [TestMethod]
    public async Task SendAsync_TestInput_TraversesInputAndBothOutputPipes()
    {
        await using var fixture = new NetworkFixture();
        await fixture.ReadyAsync();
        var request = Movement();
        await fixture.Peers.SendAsync(request);
        var output = await fixture.SnapshotAsync(snapshot => snapshot.Authorizations.Any(authorization => authorization.RequestId == request.RequestId && authorization.AuthorizedAuthorityMeters > 0));
        Assert.AreEqual(365.76, output.Authorizations.Single().AuthorizedAuthorityMeters);
        Assert.AreEqual(11.176, output.Authorizations.Single().AuthorizedSpeedMetersPerSecond);
        await UntilAsync(() => fixture.Peers.RecentMessages.Any(envelope => envelope.MessageType == nameof(TrainAuthorizationStatusMessage) && MessageSerializer.DeserializePayload<TrainAuthorizationStatusMessage>(envelope).AuthorizedAuthorityMeters == 365.76));
        Assert.IsGreaterThan(0L, fixture.Peers.CtcReceivedCount);
        Assert.IsGreaterThan(0L, fixture.Peers.TrackModelReceivedCount);
    }

    /// <summary>Verifies that shared module sender and test sender reach same receiver.</summary>
    [TestMethod]
    public async Task SendAsync_ModuleAndTestSenders_ReachSameReceiver()
    {
        await using var fixture = new NetworkFixture();
        // This is the exact Common transport now called by CTC's production sender.
        await NamedPipeTransport.SendAsync(fixture.Endpoints.Input, new MaintenanceRequestMessage { BlockId = "12", RequestedState = MaintenanceState.Closed });
        await fixture.SnapshotAsync(snapshot => snapshot.Blocks.Single(blockStatus => blockStatus.BlockId == "12").Maintenance == "Pending closure");
        await fixture.Peers.SendAsync(new MaintenanceRequestMessage { BlockId = "12", RequestedState = MaintenanceState.Open });
        await fixture.SnapshotAsync(snapshot => snapshot.Blocks.Single(blockStatus => blockStatus.BlockId == "12").Maintenance == "Open");
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
        Assert.HasCount(3, output.Authorizations[0].PathBlockIds);
        Assert.AreEqual(365.76, output.Authorizations[0].SuggestedAuthorityMeters);
    }

    /// <summary>Verifies that malformed connection does not kill shared receiver.</summary>
    [TestMethod]
    public async Task ListenAsync_MalformedConnection_ContinuesReceiving()
    {
        await using var fixture = new NetworkFixture();
        await using (var client = new NamedPipeClientStream(".", fixture.Endpoints.Input, PipeDirection.Out, PipeOptions.Asynchronous))
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
        await UntilAsync(() => Results(fixture.Peers).Any(acknowledgement => !acknowledgement.IsAccepted && acknowledgement.MessageType == nameof(WaysideMovementRequestMessage)));
        var snapshot = await fixture.SnapshotAsync(snapshot => snapshot.Authorizations.Count == 0);
        Assert.IsEmpty(snapshot.Authorizations);
    }

    /// <summary>Verifies that hardware plc and manual commands use same wire interface.</summary>
    [TestMethod]
    public async Task SendAsync_PlcAndManualCommands_UsesProductionInterface()
    {
        await using var fixture = new NetworkFixture();
        await fixture.Peers.SendAsync(new WaysidePlcProgramMessage { Name = "wire.plc", Source = "allow_route = route_safe and running\nswitch_normal = requested_normal\nsignal_clear = route_safe and running\ncrossing_close = crossing_required" });
        await fixture.SnapshotAsync(snapshot => snapshot.PlcName == "wire.plc" && snapshot.IsPaused);
        await fixture.FeedbackAsync();
        await fixture.Peers.SendAsync(new WaysideMaintenanceModeMessage { IsEnabled = true });
        await fixture.Peers.SendAsync(new WaysideManualCommandMessage { BlockId = "4", Signal = SignalState.Red });
        await UntilAsync(() => Results(fixture.Peers).Any(acknowledgement => acknowledgement.IsAccepted && acknowledgement.MessageType == nameof(WaysideManualCommandMessage)));
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
        await fixture.SnapshotAsync(snapshot => snapshot.Authorizations.Any(authorization => authorization.RequestId == request.RequestId && authorization.AuthorizedAuthorityMeters > 0));
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
        var endpoints = WaysideEndpoints.CreateIsolated();
        await using var host = new WaysidePipeHost(new TrackControllerService(), endpoints);
        host.Start();
        await using var peers = new WaysideTestConnection(endpoints); // Deliberately do not start output listeners.
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

    /// <summary>Verifies that ctc receives measured feedback and revocation after cancellation.</summary>
    [TestMethod]
    public async Task PublishAsync_CancelledMovement_RevokesCtcAuthorization()
    {
        await using var fixture = new NetworkFixture();
        await fixture.ReadyAsync();
        var request = Movement();
        await fixture.Peers.SendAsync(request);
        await UntilAsync(() => fixture.Peers.RecentMessages.Any(envelope => envelope.MessageType == nameof(TrainAuthorizationStatusMessage) && MessageSerializer.DeserializePayload<TrainAuthorizationStatusMessage>(envelope).AuthorizedAuthorityMeters > 0));
        request.IsActive = false;
        await fixture.Peers.SendAsync(request);
        await UntilAsync(() => fixture.Peers.RecentMessages.Any(envelope => envelope.MessageType == nameof(TrainAuthorizationStatusMessage) && MessageSerializer.DeserializePayload<TrainAuthorizationStatusMessage>(envelope).TrainId == "101" && MessageSerializer.DeserializePayload<TrainAuthorizationStatusMessage>(envelope).AuthorizedAuthorityMeters == 0));
        var block = fixture.Peers.RecentMessages.Where(envelope => envelope.MessageType == nameof(BlockStatusMessage)).Select(MessageSerializer.DeserializePayload<BlockStatusMessage>).Last(blockStatus => blockStatus.BlockId == "6");
        Assert.AreEqual(SwitchPosition.Normal, block.Switch);
    }

    /// <summary>Verifies that snapshot request responds through normal output pipe.</summary>
    [TestMethod]
    public async Task Receive_SnapshotRequest_UsesNormalOutputPipe()
    {
        await using var fixture = new NetworkFixture();
        await fixture.Peers.SendAsync(new WaysideSnapshotRequest());
        await fixture.SnapshotAsync(snapshot => snapshot.Blocks.Count == 12);
        Assert.IsGreaterThan(0L, fixture.Peers.TrackModelReceivedCount);
    }
}
