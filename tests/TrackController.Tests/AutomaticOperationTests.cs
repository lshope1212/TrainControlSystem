using TrackController.Core.Models;
using TrackController.Core.Services;
using TrackController.Runtime;
using TrainControl.Common.Communication;
using static TrackController.Tests.PiRuntimeTests;

namespace TrackController.Tests;

[TestClass]
public sealed class AutomaticOperationTests
{
    [TestMethod]
    public void ProductionLifecycle_StartsWithValidPlc_AndRejectsLocalTimeControls()
    {
        var config = WaysideConfiguration.CreateDemo();
        config.AutomaticOperation = true;
        var controller = new TrackControllerService(config);
        controller.LoadPlc("wayside.plc", ProgramSource);
        Assert.IsFalse(controller.GetSnapshot().IsPaused);
        controller.ApplyFeedback(Clear(config));
        controller.SubmitMovement(Movement());
        Assert.IsGreaterThan(0, controller.GetSnapshot().Authorizations.Single().AuthorizedAuthorityMeters);
        Assert.ThrowsExactly<InvalidOperationException>(() => controller.SetSimulation(true));
        Assert.AreEqual(0, controller.GetSnapshot().SimulationSeconds);
        Assert.ThrowsExactly<InvalidOperationException>(() => controller.LoadPlc("replacement", ProgramSource));
        controller.SetMaintenanceMode(true);
        Assert.AreEqual(0, controller.GetSnapshot().Authorizations.Single().AuthorizedAuthorityMeters);
        controller.LoadPlc("replacement", ProgramSource);
        Assert.IsTrue(controller.GetSnapshot().IsMaintenanceMode);
        Assert.AreEqual(0, controller.GetSnapshot().Authorizations.Single().AuthorizedAuthorityMeters);
        controller.SetMaintenanceMode(false);
        Assert.IsGreaterThan(0, controller.GetSnapshot().Authorizations.Single().AuthorizedAuthorityMeters);
        controller.SetMaintenanceMode(true);
        Assert.ThrowsExactly<ArgumentException>(() => controller.LoadPlc("broken", "invalid PLC"));
        controller.SetMaintenanceMode(false);
        Assert.AreEqual(0, controller.GetSnapshot().Authorizations.Single().AuthorizedAuthorityMeters);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProductionPi_ReconnectsWithoutTimeToggle_PreservesMaintenanceAndRequiresNewInputs(bool maintenance)
    {
        var config = WaysideConfiguration.CreateDemo();
        config.AutomaticOperation = true;
        var server = new PiRuntimeServer(0);
        server.Start();
        int port = server.Port;
        await using var remote = new PiWaysideRuntime("127.0.0.1", port, config, "wayside.plc", ProgramSource);
        try
        {
            await Send(remote, Clear(config));
            await Send(remote, Movement());
            Assert.IsGreaterThan(0, (await remote.PollAsync()).Authorizations.Single().AuthorizedAuthorityMeters);
            await Send(remote, MessageSerializer.CreateEnvelope("SetMaintenanceMode", new { enabled = maintenance }));
            await server.DisposeAsync();
            server = new PiRuntimeServer(port);
            server.Start();
            var lost = await remote.PollAsync();
            Assert.IsTrue(lost.Authorizations.All(item => item.AuthorizedAuthorityMeters == 0));
            await Task.Delay(1100);
            var reconnected = await remote.PollAsync();
            Assert.IsFalse(reconnected.IsPaused);
            Assert.AreEqual(maintenance, reconnected.IsMaintenanceMode);
            Assert.IsEmpty(reconnected.Authorizations);
            Assert.IsTrue(reconnected.Blocks.All(block => !block.IsFresh));
            await Send(remote, Clear(config));
            await Send(remote, Movement());
            var result = (await remote.PollAsync()).Authorizations.Single();
            Assert.AreEqual(maintenance, result.AuthorizedAuthorityMeters == 0);
        }
        finally { await server.DisposeAsync(); }
    }
}
