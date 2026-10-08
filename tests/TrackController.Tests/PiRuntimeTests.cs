using TrackController.Contracts.Messages;
using TrackController.Core.Models;
using TrackController.Core.Services;
using TrackController.Runtime;
using TrainControl.Contracts.Enums;

namespace TrackController.Tests;

/// <summary>Exercises PiRuntime behavior without changing live controller state.</summary>
[TestClass]
public sealed class PiRuntimeTests
{
    internal static string ProgramSource => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PiSamples", "wayside.plc"));

    internal static WaysideFeedbackMessage Clear(WaysideConfigurationMessage configuration, string occupied = "5") => new()
    {
        Blocks = configuration.Blocks.Select(blockDefinition => new WaysideBlockFeedback { BlockId = blockDefinition.BlockId, ObservedAtUtc = DateTimeOffset.UtcNow, Occupancy = blockDefinition.BlockId == occupied ? OccupancyState.Occupied : OccupancyState.Clear, Signal = SignalState.Green, Switch = SwitchPosition.Normal, Crossing = CrossingState.Closed, IsReservedByCtc = false }).ToList()
    };

    internal static WaysideMovementRequestMessage Movement(string target = "5", params string[] path) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        TrainId = "Train 101",
        TargetBlockId = target,
        PathBlockIds = path.Length == 0 ? ["6", "7", "8"] : path.ToList(),
        SuggestedSpeedMetersPerSecond = 11.176,
        SuggestedAuthorityMeters = 365.76
    };

    internal static async Task<WaysideAcknowledgementMessage> Send(IWaysideRuntime runtime, object message)
    {
        var result = await runtime.ReceiveAsync(RuntimeMessages.CreateEnvelope(message));
        return (WaysideAcknowledgementMessage)result;
    }

    private static LocalWaysideRuntime Local(WaysideConfigurationMessage configuration)
    {
        var controller = new TrackControllerService(configuration);
        controller.LoadPlc("wayside.plc", ProgramSource);
        return new(controller);
    }

    /// <summary>Verifies that pc and network runtime use same program and return same movement.</summary>
    [TestMethod]
    public async Task ReceiveAsync_SameInputsOnPcAndPi_ReturnsSameMovement()
    {
        await using var server = new PiRuntimeServer(0);
        server.Start();
        var config = WaysideConfiguration.CreateDemo();
        await using var remote = new PiWaysideRuntime("127.0.0.1", server.Port, config, "wayside.plc", ProgramSource);
        await using var local = Local(config);
        Assert.IsTrue((await remote.PollAsync()).IsPaused);
        var request = Movement();
        foreach (IWaysideRuntime runtime in new IWaysideRuntime[]
        {
            local,
            remote
        }

        )
        {
            var feedback = Clear(config);
            feedback.Blocks.ForEach(block => block.Signal = SignalState.SuperGreen);
            Assert.IsTrue((await Send(runtime, feedback)).IsAccepted);
            Assert.IsTrue((await Send(runtime, new WaysideSimulationMessage { IsPaused = false })).IsAccepted);
            Assert.IsTrue((await Send(runtime, request)).IsAccepted);
        }

        var actual = await remote.PollAsync();
        var expected = await local.PollAsync();
        Assert.AreEqual(365.76, actual.Authorizations.Single().AuthorizedAuthorityMeters, 0.000001);
        Assert.AreEqual(WaysideMovementRequestMessage.BlockEntryReference, actual.Authorizations.Single().AuthorityReference);
        Assert.IsTrue(actual.Blocks.Where(block => block.HasSignal).All(block => block.ReportedSignal == SignalState.SuperGreen));
        Assert.AreEqual(expected.Authorizations.Single().AuthorizedSpeedMetersPerSecond, actual.Authorizations.Single().AuthorizedSpeedMetersPerSecond);
        Assert.AreEqual(expected.Authorizations.Single().Status, actual.Authorizations.Single().Status);
        Assert.IsTrue(actual.Blocks.All(blockStatus => blockStatus.IsFresh));
        Assert.IsTrue(actual.ValidUntilUtc > DateTimeOffset.UtcNow);
        Assert.IsTrue(actual.ValidUntilUtc <= DateTimeOffset.UtcNow.AddSeconds(1.5));
    }

    /// <summary>Verifies that disconnect revokes pi output and reconnect starts paused with no old requests.</summary>
    [TestMethod]
    public async Task PollAsync_DisconnectedPi_RevokesAuthorityAndReconnectsPaused()
    {
        var server = new PiRuntimeServer(0);
        server.Start();
        int port = server.Port;
        var config = WaysideConfiguration.CreateDemo();
        await using var remote = new PiWaysideRuntime("127.0.0.1", port, config, "wayside.plc", ProgramSource);
        try
        {
            await Send(remote, Clear(config));
            await Send(remote, new WaysideSimulationMessage { IsPaused = false });
            await Send(remote, Movement());
            Assert.AreEqual(365.76, (await remote.PollAsync()).Authorizations.Single().AuthorizedAuthorityMeters, 0.000001);
            await server.DisposeAsync();
            server = new PiRuntimeServer(port);
            server.Start();
            var lost = await remote.PollAsync();
            Assert.IsTrue(lost.Blocks.All(blockStatus => !blockStatus.IsFresh && blockStatus.CommandedSignal == SignalState.Red));
            Assert.IsTrue(lost.Authorizations.All(authorization => authorization.AuthorizedAuthorityMeters == 0));
            await Task.Delay(1100);
            var fresh = await remote.PollAsync();
            Assert.IsTrue(fresh.IsPaused);
            Assert.IsEmpty(fresh.Authorizations);
            Assert.IsTrue(fresh.Blocks.All(blockStatus => !blockStatus.IsFresh));
            await Send(remote, Clear(config));
            await Send(remote, Movement());
            Assert.AreEqual(0, (await remote.PollAsync()).Authorizations.Single().AuthorizedAuthorityMeters);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    /// <summary>Verifies that pi has exactly one owner and rejects software bypass.</summary>
    [TestMethod]
    public async Task ReceiveAsync_SecondOwnerOrSoftwareBypass_RejectsRequest()
    {
        await using var server = new PiRuntimeServer(0);
        server.Start();
        var config = WaysideConfiguration.CreateDemo();
        await using var first = new PiWaysideRuntime("127.0.0.1", server.Port, config, "wayside.plc", ProgramSource);
        await using var second = new PiWaysideRuntime("127.0.0.1", server.Port, config, "wayside.plc", ProgramSource);
        await first.PollAsync();
        var busy = await second.PollAsync();
        StringAssert.Contains(busy.PlcStatus, "already assigned");
        Assert.IsFalse((await Send(first, new WaysideControllerModeMessage { IsHardwareMode = false })).IsAccepted);
        Assert.IsTrue((await first.PollAsync()).IsHardwareMode);
    }

    /// <summary>Verifies that router partitions inputs rejects unknown and cross territory routes.</summary>
    [TestMethod]
    public async Task ReceiveAsync_UnknownOrForeignRoute_RejectsRequest()
    {
        await using var server = new PiRuntimeServer(0);
        server.Start();
        var software = Section("SW", "1", "2", "3");
        var hardware = Section("HW", "4", "5", "6");
        var local = Local(software);
        var remote = new PiWaysideRuntime("127.0.0.1", server.Port, hardware, "wayside.plc", ProgramSource);
        await using var router = new TerritoryRuntimeRouter("Both", [new(software, "Local", local), new(hardware, "Pi", remote)]);
        var combined = new WaysideFeedbackMessage
        {
            Blocks = Clear(software, "1").Blocks.Concat(Clear(hardware, "4").Blocks).ToList()
        };
        Assert.IsTrue((await Send(router, combined)).IsAccepted);
        Assert.IsTrue((await router.PollAsync()).Blocks.All(blockStatus => blockStatus.IsFresh));
        Assert.IsTrue((await Send(router, new WaysideSimulationMessage { IsPaused = false })).IsAccepted);
        Assert.IsTrue((await Send(router, Movement("1", "2", "3"))).IsAccepted);
        Assert.IsEmpty((await remote.PollAsync()).Authorizations, "The PC request must not be duplicated on the Pi.");
        Assert.IsTrue((await Send(router, Movement("4", "5", "6"))).IsAccepted);
        Assert.HasCount(1, (await local.PollAsync()).Authorizations);
        Assert.HasCount(1, (await remote.PollAsync()).Authorizations);
        Assert.IsFalse((await Send(router, Movement("3", "4"))).IsAccepted);
        combined.Blocks[0].Occupancy = OccupancyState.Unknown;
        combined.Blocks.Add(new()
        {
            BlockId = "unowned"
        });
        Assert.IsFalse((await Send(router, combined)).IsAccepted);
        Assert.AreEqual(OccupancyState.Occupied, (await local.PollAsync()).Blocks[0].Occupancy, "Reject the entire invalid batch before forwarding any part.");
        Assert.IsFalse((await Send(router, new WaysideControllerModeMessage { IsHardwareMode = false })).IsAccepted);
    }

    /// <summary>Verifies that shared program replacement pauses both targets and withholds movement.</summary>
    [TestMethod]
    public async Task LoadPlc_ReplacementProgram_PausesBothTargets()
    {
        await using var server = new PiRuntimeServer(0);
        server.Start();
        var software = Section("SW", "1", "2");
        var hardware = Section("HW", "4", "5");
        await using var router = new TerritoryRuntimeRouter("Both", [new(software, "Local", Local(software)), new(hardware, "Pi", new PiWaysideRuntime("127.0.0.1", server.Port, hardware, "wayside.plc", ProgramSource))]);
        await router.PollAsync();
        var result = await Send(router, new WaysidePlcProgramMessage { Name = "deny.plc", Source = "allow_route = false\nsignal_clear = false\nswitch_normal = requested_normal\ncrossing_close = true" });
        Assert.IsTrue(result.IsAccepted);
        var snapshot = await router.PollAsync();
        Assert.IsTrue(snapshot.IsPaused);
        Assert.AreEqual("deny.plc", snapshot.PlcName);
    }

    /// <summary>Verifies that duplicate block owners and non tunnel endpoints are rejected.</summary>
    [TestMethod]
    public void Load_DuplicateOwnersOrNonLoopbackHost_RejectsDeployment()
    {
        var config = Section("A", "1");
        Assert.ThrowsExactly<ArgumentException>(() => new TerritoryRuntimeRouter("Bad", [new(config, "Local", Local(config)), new(config, "Local", Local(config))]));
        Assert.ThrowsExactly<ArgumentException>(() => new PiWaysideRuntime("192.168.50.2", 11405, config, "wayside.plc", ProgramSource));
    }

    /// <summary>Verifies that idle connection publishes stop without another pc message.</summary>
    [TestMethod]
    public async Task PollAsync_IdlePcConnection_PublishesStop()
    {
        await using var server = new PiRuntimeServer(0);
        server.Start();
        var stopped = new TaskCompletionSource<WaysideSnapshotMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SnapshotProduced += snapshot =>
        {
            if (snapshot.PlcStatus.Contains("PC connection ended"))
            {
                stopped.TrySetResult(snapshot);
            }
        };
        var config = WaysideConfiguration.CreateDemo();
        await using var remote = new PiWaysideRuntime("127.0.0.1", server.Port, config, "wayside.plc", ProgramSource);
        await Send(remote, Clear(config));
        await Send(remote, new WaysideSimulationMessage { IsPaused = false });
        await Send(remote, Movement());
        Assert.AreEqual(365.76, (await remote.PollAsync()).Authorizations.Single().AuthorizedAuthorityMeters, 0.000001);
        var lost = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(lost.IsPaused);
        Assert.IsTrue(lost.Authorizations.All(authorization => authorization.AuthorizedAuthorityMeters == 0));
        Assert.IsTrue(lost.Blocks.All(blockStatus => !blockStatus.IsFresh));
    }

    /// <summary>Verifies that invalid replacement cannot restore previous program on reconnect.</summary>
    [TestMethod]
    public async Task LoadPlc_InvalidReplacement_DoesNotRestoreOldProgram()
    {
        var server = new PiRuntimeServer(0);
        server.Start();
        int port = server.Port;
        var config = WaysideConfiguration.CreateDemo();
        await using var remote = new PiWaysideRuntime("127.0.0.1", port, config, "wayside.plc", ProgramSource);
        try
        {
            await remote.PollAsync();
            Assert.IsFalse((await Send(remote, new WaysidePlcProgramMessage { Name = "invalid.plc", Source = "allow_route = missing_variable" })).IsAccepted);
            await server.DisposeAsync();
            server = new PiRuntimeServer(port);
            server.Start();
            await remote.PollAsync();
            await Task.Delay(1100);
            var reconnected = await remote.PollAsync();
            Assert.IsTrue(reconnected.IsPaused);
            Assert.IsTrue(reconnected.Blocks.All(blockStatus => !blockStatus.IsFresh && blockStatus.CommandedSignal == SignalState.Red));
            Assert.IsTrue(reconnected.Authorizations.All(authorization => authorization.AuthorizedAuthorityMeters == 0));
            StringAssert.Contains(reconnected.PlcStatus, "Pi unavailable");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    private static WaysideConfigurationMessage Section(string id, params string[] blocks) => new()
    {
        WaysideId = id,
        Blocks = blocks.Select((blockId, index) => new WaysideBlockDefinition { BlockId = blockId, LengthMeters = 100, SpeedLimitMetersPerSecond = 15, NextBlockIds = index + 1 < blocks.Length ? [blocks[index + 1]] : [] }).ToList()
    };
}
