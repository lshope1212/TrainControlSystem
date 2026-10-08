using System.Text.Json.Nodes;
using System.Text.Json;
using TrackController.Contracts.Messages;
using TrackController.Core.Models;
using TrackController.Core.Services;
using TrackController.Runtime;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Tests;

/// <summary>Exercises TerritoryDeployment behavior without changing live controller state.</summary>
[TestClass]
public sealed class TerritoryDeploymentTests
{
    internal static TerritoryAssignment[] Choices(PreparedDeployment plan, string second = "Local", int port = 11405) => plan.Territories.Select((territory, index) => new TerritoryAssignment { TerritoryId = territory.Id, Execution = index == 1 ? second : "Local", Host = "127.0.0.1", Port = index == 1 ? port : 11406 }).ToArray();

    /// <summary>Verifies that shared map preserves boundary and every block has one command owner.</summary>
    [TestMethod]
    public async Task Read_SharedTrackMap_PreservesEdgesAndAssignsOneOwner()
    {
        using var files = new DeploymentFiles();
        var plan = files.Read();
        Assert.HasCount(12, plan.Territories[0].Configuration.Blocks);
        CollectionAssert.AreEqual(new[] { "1", "2", "3" }, WaysideConfiguration.GetOwnedIds(plan.Territories[0].Configuration).ToArray());
        CollectionAssert.AreEqual(new[] { "4" }, WaysideConfiguration.GetNeighborIds(plan.Territories[0].Configuration).ToArray());
        CollectionAssert.AreEqual(new[] { "3" }, WaysideConfiguration.GetNeighborIds(plan.Territories[1].Configuration).ToArray());
        await using var router = plan.CreateRuntime();
        var snapshot = await router.PollAsync();
        Assert.HasCount(12, snapshot.Blocks);
        Assert.HasCount(12, snapshot.Blocks.Select(blockStatus => blockStatus.BlockId).Distinct().ToArray());
        CollectionAssert.Contains(snapshot.Blocks.Single(blockStatus => blockStatus.BlockId == "3").NextBlockIds.ToArray(), "4");
        Assert.IsFalse((await PiRuntimeTests.Send(router, PiRuntimeTests.Movement("3", "4"))).IsAccepted);
    }

    /// <summary>Verifies that invalid ownership and split protection zone are rejected without editing topology.</summary>
    [TestMethod]
    public void Read_InvalidOwnershipOrProtectionZone_RejectsDeployment()
    {
        foreach (string fault in new[]
        {
            "missing",
            "duplicate",
            "unknown",
            "split switch"
        }

        )
        {
            using var files = new DeploymentFiles();
            string map = File.ReadAllText(files.PathFor("track-map.json"));
            files.Edit("territories.json", json =>
            {
                var first = json["territories"]![0]!["blockIds"]!.AsArray();
                var second = json["territories"]![1]!["blockIds"]!.AsArray();
                if (fault == "missing")
                {
                    first.RemoveAt(0);
                }

                if (fault == "duplicate")
                {
                    second.Add("1");
                }

                if (fault == "unknown")
                {
                    first.Add("999");
                }

                if (fault == "split switch")
                {
                    first.Add("5");
                    second.RemoveAt(1);
                }
            });
            Assert.ThrowsExactly<ArgumentException>(() => files.Read(), fault);
            Assert.AreEqual(map, File.ReadAllText(files.PathFor("track-map.json")));
        }
    }

    /// <summary>Verifies that neighbor feedback is shared but foreign commands and authority are rejected.</summary>
    [TestMethod]
    public async Task ReceiveAsync_NeighborFeedback_ObservesWithoutForeignAuthority()
    {
        using var files = new DeploymentFiles();
        var plan = files.Read();
        var entries = plan.Territories.Select(territory =>
        {
            var service = new TrackControllerService(territory.Configuration);
            service.LoadPlc(plan.ProgramName, plan.ProgramSource);
            return new TerritoryRuntimeRouter.Entry(territory.Configuration, "Local", new LocalWaysideRuntime(service));
        }).ToArray();
        await using var router = new TerritoryRuntimeRouter("Shared", entries);
        var feedback = PiRuntimeTests.Clear(plan.Territories[0].Configuration, "4");
        Assert.IsTrue((await PiRuntimeTests.Send(router, feedback)).IsAccepted);
        var west = await entries[0].Runtime.PollAsync();
        Assert.HasCount(3, west.Blocks);
        Assert.AreEqual(OccupancyState.Occupied, west.NeighborObservations.Single().Occupancy);
        Assert.IsTrue(west.NeighborObservations.Single().IsFresh);
        Assert.IsFalse((await PiRuntimeTests.Send(entries[0].Runtime, PiRuntimeTests.Movement("4", "5"))).IsAccepted);
        Assert.IsFalse((await PiRuntimeTests.Send(entries[0].Runtime, new MaintenanceRequestMessage { BlockId = "4", RequestedState = MaintenanceState.Closed })).IsAccepted);
        Assert.IsFalse((await PiRuntimeTests.Send(entries[0].Runtime, new SwitchPositionRequestMessage { BlockId = "6", RequestedPosition = SwitchPosition.Reverse })).IsAccepted);
        await PiRuntimeTests.Send(router, new BlockStatusMessage { BlockId = "4", Occupancy = OccupancyState.Clear });
        west = await entries[0].Runtime.PollAsync();
        Assert.AreEqual(OccupancyState.Clear, west.NeighborObservations.Single().Occupancy);
        Assert.IsEmpty(west.Authorizations);
        var unavailable = UnavailableOutput.CreateStopSnapshot(west, "Lost");
        Assert.IsFalse(unavailable.NeighborObservations.Single().IsFresh);
    }

    /// <summary>Verifies that moving execution to pi keeps ownership topology program and movement results.</summary>
    [TestMethod]
    public async Task WithExecution_PiAssignment_PreservesOwnershipAndResults()
    {
        using var files = new DeploymentFiles();
        var plan = files.Read();
        await using var server = new PiRuntimeServer(0);
        server.Start();
        var moved = plan.WithExecution(Choices(plan, "Pi", server.Port));
        Assert.AreEqual(JsonSerializer.Serialize(plan.Territories[1].Configuration), JsonSerializer.Serialize(moved.Territories[1].Configuration));
        await using var local = plan.CreateRuntime();
        await using var mixed = moved.CreateRuntime();
        foreach (var runtime in new[]
        {
            local,
            mixed
        }

        )
        {
            Assert.IsTrue((await PiRuntimeTests.Send(runtime, PiRuntimeTests.Clear(plan.Territories[0].Configuration))).IsAccepted);
            Assert.IsTrue((await PiRuntimeTests.Send(runtime, new WaysideSimulationMessage { IsPaused = false })).IsAccepted);
            Assert.IsTrue((await PiRuntimeTests.Send(runtime, PiRuntimeTests.Movement())).IsAccepted);
            Assert.AreEqual(365.76, (await runtime.PollAsync()).Authorizations.Single().AuthorizedAuthorityMeters, 0.000001);
        }

        Assert.AreEqual(plan.ProgramSource, moved.ProgramSource);
        Assert.IsTrue(files.Read().Territories.All(territory => territory.Execution == "Local"), "Editing execution is not an implicit file save.");
    }

    /// <summary>Verifies that save placement does not rewrite ownership or map and rejects external changes.</summary>
    [TestMethod]
    public void SaveExecution_ChangedPlacement_PreservesMapAndRejectsExternalEdits()
    {
        using var files = new DeploymentFiles();
        var plan = files.Read();
        var original = new[]
        {
            "track-map.json",
            "territories.json",
            "wayside.plc"
        }.ToDictionary(name => name, name => File.ReadAllText(files.PathFor(name)));
        var changed = plan.WithExecution(Choices(plan, "Pi"));
        changed.SaveExecution();
        Assert.AreEqual("Pi", files.Read().Territories[1].Execution);
        foreach (var file in original)
        {
            Assert.AreEqual(file.Value, File.ReadAllText(files.PathFor(file.Key)));
        }

        files.Edit("all-pc.deployment.json", json => json["name"] = "External edit");
        Assert.ThrowsExactly<IOException>(changed.SaveExecution);
        Assert.AreEqual("External edit", files.Read().Name);
    }

    /// <summary>Verifies that legacy deployment still loads and duplicate pi ports are rejected.</summary>
    [TestMethod]
    public async Task Read_LegacyDeployment_LoadsAndRejectsDuplicatePorts()
    {
        using var files = new DeploymentFiles();
        var legacy = files.Read("legacy-pc-and-pi.deployment.json");
        await using var runtime = legacy.WithExecution(Choices(legacy)).CreateRuntime();
        Assert.HasCount(12, (await runtime.PollAsync()).Blocks);
        var plan = files.Read();
        var choices = Choices(plan, "Pi");
        choices[0].Execution = "Pi";
        choices[0].Port = choices[1].Port;
        Assert.ThrowsExactly<ArgumentException>(() => plan.WithExecution(choices));
    }

    /// <summary>Verifies that pi reports neighbor feedback without publishing foreign commands.</summary>
    [TestMethod]
    public async Task PollAsync_NeighborBlock_ReportsFeedbackWithoutCommands()
    {
        using var files = new DeploymentFiles();
        var plan = files.Read();
        await using var server = new PiRuntimeServer(0);
        server.Start();
        var config = plan.Territories[1].Configuration;
        await using var remote = new PiWaysideRuntime("127.0.0.1", server.Port, config, plan.ProgramName, plan.ProgramSource);
        var feedback = PiRuntimeTests.Clear(config, "3");
        feedback.Blocks = feedback.Blocks.Where(feedback => int.Parse(feedback.BlockId) >= 3).ToList();
        Assert.IsTrue((await PiRuntimeTests.Send(remote, feedback)).IsAccepted);
        var snapshot = await remote.PollAsync();
        Assert.HasCount(9, snapshot.Blocks);
        Assert.IsFalse(snapshot.Blocks.Any(blockStatus => blockStatus.BlockId == "3"));
        Assert.AreEqual("3", snapshot.NeighborObservations.Single().BlockId);
        Assert.AreEqual(OccupancyState.Occupied, snapshot.NeighborObservations.Single().Occupancy);
        Assert.IsFalse((await PiRuntimeTests.Send(remote, PiRuntimeTests.Movement("3", "4"))).IsAccepted);
    }

    /// <summary>Verifies that old pi protocol is rejected before sending ownership configuration.</summary>
    [TestMethod]
    public async Task Connect_OldProtocolVersion_RejectsBeforeConfiguration()
    {
        using var files = new DeploymentFiles();
        var plan = files.Read();
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        await using var remote = new PiWaysideRuntime("127.0.0.1", port, plan.Territories[1].Configuration, plan.ProgramName, plan.ProgramSource);
        var pending = remote.PollAsync();
        using var socket = await accept;
        using var writer = new StreamWriter(socket.GetStream(), new System.Text.UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
        await writer.WriteLineAsync(TrainControl.Common.Communication.MessageSerializer.Serialize(new PiHello(1, DateTimeOffset.UtcNow)));
        var result = await pending;
        Assert.IsTrue(result.IsPaused);
        Assert.IsTrue(result.Blocks.All(blockStatus => !blockStatus.IsFresh));
        StringAssert.Contains(result.PlcStatus, "protocol version mismatch");
        byte[] buffer = new byte[1];
        Assert.AreEqual(0, await socket.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(2)), "An old Pi must never receive a full-map session it could mistake for owned blocks.");
    }

    /// <summary>Verifies that invalid pi session is closed and next owner can connect.</summary>
    [TestMethod]
    public async Task Connect_InvalidSession_ReleasesOwnershipForNextClient()
    {
        using var files = new DeploymentFiles();
        var plan = files.Read();
        await using var server = new PiRuntimeServer(0);
        server.Start();
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.StatusChanged += status =>
        {
            if (status.Contains("initialization required"))
            {
                rejected.TrySetResult();
            }
        };
        using var invalid = new System.Net.Sockets.TcpClient();
        await invalid.ConnectAsync("127.0.0.1", server.Port);
        using var reader = new StreamReader(invalid.GetStream(), leaveOpen: true);
        await reader.ReadLineAsync();
        using var writer = new StreamWriter(invalid.GetStream(), new System.Text.UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
        await writer.WriteLineAsync(TrainControl.Common.Communication.MessageSerializer.Serialize(new WaysideSnapshotRequest()));
        await rejected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsNull(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        await using var remote = new PiWaysideRuntime("127.0.0.1", server.Port, plan.Territories[1].Configuration, plan.ProgramName, plan.ProgramSource);
        var snapshot = await remote.PollAsync();
        Assert.AreEqual(plan.ProgramName, snapshot.PlcName);
        Assert.IsTrue(snapshot.IsPaused);
        Assert.HasCount(9, snapshot.Blocks);
    }
}
