using TrackController.Contracts.Messages;
using TrackController.Core.Services;
using TrackController.Runtime;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Messages;
using TrainControl.Contracts.Enums;

namespace TrackController.Tests;

[TestClass]
public sealed class LocalWaysideRuntimeTests
{
    /// <summary>Dispatches to the service and publishes the resulting state immediately.</summary>
    [TestMethod]
    public async Task ReceiveAsync_AcceptedMessage_RefreshesLatestSnapshot()
    {
        await using var runtime = new LocalWaysideRuntime(new TrackControllerService());
        var message = MessageSerializer.CreateEnvelope("SetMaintenanceMode", new { enabled = true });
        var result = (WaysideAcknowledgementMessage)await runtime.ReceiveAsync(MessageSerializer.Deserialize(MessageSerializer.Serialize(message)));

        Assert.IsTrue(result.IsAccepted);
        Assert.AreEqual("SetMaintenanceMode", result.MessageType);
        Assert.IsTrue(runtime.LatestSnapshot.IsMaintenanceMode);
        var snapshot = (WaysideSnapshotMessage)await runtime.ReceiveAsync(MessageSerializer.Deserialize(MessageSerializer.Serialize(MessageSerializer.CreateEnvelope("GetSnapshot", new { }))));
        Assert.IsTrue(snapshot.IsMaintenanceMode);
        Assert.AreEqual(runtime.LatestSnapshot.WaysideId, snapshot.WaysideId);
    }

    /// <summary>A canceled input must not mutate controller state.</summary>
    [TestMethod]
    public async Task ReceiveAsync_CanceledBeforeDispatch_LeavesStateUnchanged()
    {
        var controller = new TrackControllerService();
        await using var runtime = new LocalWaysideRuntime(controller);
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => runtime.ReceiveAsync(MessageSerializer.Deserialize(MessageSerializer.Serialize(MessageSerializer.CreateEnvelope("SetMaintenanceMode", new { enabled = true }))), stop.Token));
        Assert.IsFalse(controller.GetSnapshot().IsMaintenanceMode);
        Assert.IsFalse(runtime.LatestSnapshot.IsMaintenanceMode);
    }

    /// <summary>Malformed input does not prevent the runtime from processing the next valid message.</summary>
    [TestMethod]
    public async Task ReceiveAsync_InvalidPayload_RejectsAndContinues()
    {
        await using var runtime = new LocalWaysideRuntime(new TrackControllerService());
        var invalid = MessageSerializer.Deserialize("{\"messageType\":\"SetMaintenanceMode\",\"payload\":{\"enabled\":\"invalid\"}}");
        var rejected = (WaysideAcknowledgementMessage)await runtime.ReceiveAsync(invalid);
        Assert.IsFalse(rejected.IsAccepted);
        Assert.IsFalse(runtime.LatestSnapshot.IsMaintenanceMode);
        var accepted = (WaysideAcknowledgementMessage)await runtime.ReceiveAsync(MessageSerializer.Deserialize(MessageSerializer.Serialize(MessageSerializer.CreateEnvelope("SetMaintenanceMode", new { enabled = true }))));
        Assert.IsTrue(accepted.IsAccepted);
        Assert.IsTrue(runtime.LatestSnapshot.IsMaintenanceMode);
    }

    [TestMethod]
    [DataRow(false, "brokenRail")]
    [DataRow(false, "trackCircuitFailure")]
    [DataRow(false, "powerFailure")]
    [DataRow(false, "isClosed")]
    [DataRow(true, "brokenRail")]
    [DataRow(true, "trackCircuitFailure")]
    [DataRow(true, "powerFailure")]
    [DataRow(true, "isClosed")]
    public async Task ReceiveAsync_SharedTrackModelState_RevokesMovement(bool hardware, string failure)
    {
        var controller = new TrackControllerService();
        controller.SetHardwareMode(hardware);
        if (hardware) controller.LoadPlc("wayside.plc", PiRuntimeTests.ProgramSource);
        await using var runtime = new LocalWaysideRuntime(controller);
        var states = PiRuntimeTests.Clear(controller.GetConfiguration());
        // Exercise the actual Track Model's single-block contract, not only the test batch.
        foreach (var state in states)
            Assert.IsTrue((await PiRuntimeTests.Send(runtime, state)).IsAccepted);
        controller.SetSimulation(false);
        Assert.IsTrue((await PiRuntimeTests.Send(runtime, PiRuntimeTests.Movement())).IsAccepted);
        Assert.IsGreaterThan(0d, runtime.LatestSnapshot.Authorizations.Single().AuthorizedAuthorityMeters);

        var failing = states.Single(block => block.BlockId == "5") with
        {
            ObservedAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(1),
            BrokenRail = failure == "brokenRail",
            TrackCircuitFailure = failure == "trackCircuitFailure",
            PowerFailure = failure == "powerFailure",
            IsClosed = failure == "isClosed"
        };
        Assert.IsTrue((await PiRuntimeTests.Send(runtime, failing)).IsAccepted);
        Assert.AreEqual(0d, runtime.LatestSnapshot.Authorizations.Single().AuthorizedAuthorityMeters);
        var status = runtime.LatestSnapshot.Blocks.Single(block => block.BlockId == "5");
        Assert.IsTrue(failure == "isClosed" ? status.Maintenance == "Closed" : status.IsFaulted);
    }

    [TestMethod]
    public async Task ReceiveAsync_MissingControlValue_RejectsWithoutChangingMode()
    {
        await using var runtime = new LocalWaysideRuntime(new TrackControllerService());
        var result = (WaysideAcknowledgementMessage)await runtime.ReceiveAsync(MessageSerializer.CreateEnvelope("SetMaintenanceMode", new { }));
        Assert.IsFalse(result.IsAccepted);
        Assert.IsFalse(runtime.LatestSnapshot.IsMaintenanceMode);
    }
}
