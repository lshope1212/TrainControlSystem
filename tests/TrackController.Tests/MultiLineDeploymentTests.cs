using System.Text.Json.Nodes;
using TrackController.Contracts.Messages;
using TrackController.Core.Models;
using TrackController.Runtime;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Tests;

/// <summary>A deployment spanning the Blue, Red and Green Lines: one map and ownership file per line, routed as one controller.</summary>
[TestClass]
public sealed class MultiLineDeploymentTests
{
    private const string Course = "course-territories-all-pc.deployment.json";

    [TestMethod]
    public async Task Read_CourseDeployment_OwnsEveryBlockOfEveryLineOnce()
    {
        using var files = new DeploymentFiles();
        var plan = files.Read(Course);
        CollectionAssert.AreEqual(new[] { "Blue Line", "Red Line", "Green Line" }, plan.Territories.Select(territory => territory.Configuration.LineName).Distinct().ToArray());
        CollectionAssert.AreEqual(new[] { 15, 76, 150 }, plan.Territories.GroupBy(territory => territory.Configuration.LineName).Select(line => line.Sum(territory => WaysideConfiguration.GetOwnedIds(territory.Configuration).Count)).ToArray());
        Assert.IsTrue(plan.Territories.GroupBy(territory => territory.Configuration.LineName).All(line => line.Count() == (line.Key == "Blue Line" ? 2 : line.Key == "Red Line" ? 6 : 8)));
        StringAssert.StartsWith(plan.LayoutDescription, "3 lines");

        await using var router = plan.CreateRuntime();
        var snapshot = await router.PollAsync();
        Assert.HasCount(241, snapshot.Blocks);
        Assert.HasCount(241, snapshot.Blocks.Select(block => block.BlockId).Distinct().ToArray());
        Assert.AreEqual("Blue Line / Red Line / Green Line", snapshot.LineName);
    }

    [TestMethod]
    public void LinePresets_MatchCourseOwnershipAndKeepRedAndGreenTerritoriesConnected()
    {
        using var files = new DeploymentFiles();
        var combined = files.Read(Course);
        foreach (string line in new[] { "blue", "red", "green" })
        {
            var single = files.Read($"{line}-line-all-pc.deployment.json");
            foreach (var territory in single.Territories)
            {
                var match = combined.Territories.Single(item => item.Id == territory.Id);
                var owned = WaysideConfiguration.GetOwnedIds(territory.Configuration);
                CollectionAssert.AreEquivalent(WaysideConfiguration.GetOwnedIds(match.Configuration).ToArray(), owned.ToArray());
                Assert.IsNotEmpty(WaysideConfiguration.GetNeighborIds(territory.Configuration));
                if (line is "red" or "green")
                {
                    var edges = territory.Configuration.Blocks.Where(block => owned.Contains(block.BlockId))
                        .SelectMany(block => block.NextBlockIds.Where(owned.Contains)
                            .Select(next => (From: block.BlockId, To: next))).ToArray();
                    var remaining = owned.ToHashSet();
                    var pending = new Queue<string>();
                    pending.Enqueue(owned.First());
                    remaining.Remove(pending.Peek());
                    // This checks physical connectivity, regardless of permitted travel direction.
                    while (pending.TryDequeue(out string? current))
                    {
                        foreach (var (from, to) in edges)
                        {
                            if (from == current && remaining.Remove(to)) pending.Enqueue(to);
                            if (to == current && remaining.Remove(from)) pending.Enqueue(from);
                        }
                    }
                    Assert.IsEmpty(remaining, $"{territory.Name} contains disconnected track.");
                }
                // Loading enforces complete switch/crossing zones. Verify that the full
                // physical map is also retained so boundary routes remain traversable.
                CollectionAssert.AreEquivalent(match.Configuration.Blocks.Select(block => block.BlockId).ToArray(),
                    territory.Configuration.Blocks.Select(block => block.BlockId).ToArray());
            }
        }
        CollectionAssert.AreEqual(new[] { 7, 8, 17, 13, 11, 14, 9, 12, 20, 28, 18, 22, 28, 13, 13, 8 },
            combined.Territories.Select(item => WaysideConfiguration.GetOwnedIds(item.Configuration).Count).ToArray());
    }

    [TestMethod]
    public void DirectPiAssignments_AllowDifferentDevicesButRejectSharedRuntimeEndpoint()
    {
        using var files = new DeploymentFiles();
        var plan = files.Read(Course);
        var choices = plan.Territories.Select((item, index) => new TerritoryAssignment
        {
            TerritoryId = item.Id, Execution = "Pi", Host = $"192.168.50.{index + 2}", Port = 11405
        }).ToArray();
        var prepared = plan.WithExecution(choices);
        Assert.HasCount(16, prepared.Territories);
        choices[1].Host = choices[0].Host;
        Assert.ThrowsExactly<ArgumentException>(() => plan.WithExecution(choices));
        choices[1].Port = 11406;
        Assert.HasCount(16, plan.WithExecution(choices).Territories);
    }

    [TestMethod]
    public void Read_InvalidMultiLineDeployments_AreRejectedBeforeAnyControllerStarts()
    {
        var faults = new Dictionary<string, Action<DeploymentFiles>>
        {
            // The same map twice: a line may appear once.
            ["line twice"] = files => files.Edit(Course, json => json["lines"]![2] = JsonNode.Parse("""{"trackFile":"blue-line-track-map.json","territoriesFile":"blue-line-project-territories.json"}""")),
            // A second line reusing Blue's block IDs: IDs must be unique across lines.
            ["colliding block IDs"] = files =>
            {
                File.Copy(files.PathFor("blue-line-track-map.json"), files.PathFor("copy-track-map.json"));
                files.Edit("copy-track-map.json", json => json["lineName"] = "Copy Line");
                File.Copy(files.PathFor("blue-line-project-territories.json"), files.PathFor("copy-territories.json"));
                files.Edit("copy-territories.json", json => json["territories"]![0]!["id"] = "wayside-copy");
                files.Edit(Course, json =>
                {
                    json["lines"]!.AsArray().Add(JsonNode.Parse("""{"trackFile":"copy-track-map.json","territoriesFile":"copy-territories.json"}"""));
                    json["territories"]!.AsArray().Add(JsonNode.Parse("""{"territoryId":"wayside-copy","execution":"Local"}"""));
                });
            },
            ["territory ID reused by another line"] = files => files.Edit("red-line-project-territories.json", json => json["territories"]![0]!["id"] = "wayside-blue"),
            ["missing assignment"] = files => files.Edit(Course, json => json["territories"]!.AsArray().RemoveAt(2)),
            ["unowned block"] = files => files.Edit("green-line-project-territories.json", json => json["territories"]![0]!["blockIds"]!.AsArray().RemoveAt(0)),
            ["block owned on the wrong line"] = files => files.Edit("red-line-project-territories.json", json => json["territories"]![0]!["blockIds"]!.AsArray().Add("GREEN-1")),
            ["lines mixed with trackFile"] = files => files.Edit(Course, json => json["trackFile"] = "blue-line-track-map.json"),
        };

        foreach (var (fault, apply) in faults)
        {
            using var files = new DeploymentFiles();
            apply(files);
            Assert.ThrowsExactly<ArgumentException>(() => files.Read(Course), fault);
        }
    }

    [TestMethod]
    public async Task ReceiveAsync_CommandsAndFeedback_ReachOnlyTheOwningLinesTerritory()
    {
        using var files = new DeploymentFiles();
        await using var router = files.Read(Course).CreateRuntime();
        var now = DateTimeOffset.UtcNow;

        // One Track Model batch per line, and a mixed one: each block's feedback reaches its owner only.
        foreach (var blocks in new[] { new[] { "RED-1", "RED-2" }, new[] { "GREEN-150" }, new[] { "1", "RED-76", "GREEN-1" } })
        {
            var batch = MessageSerializer.CreateEnvelope("TrackModelBlockStates", new { blocks = blocks.Select(id => new TrackModelBlockStateMessage { BlockId = id, ObservedAtUtc = now, Occupancy = OccupancyState.Clear }).ToList() });
            Assert.IsTrue(((WaysideAcknowledgementMessage)await router.ReceiveAsync(batch)).IsAccepted);
        }

        var snapshot = await router.PollAsync();
        var fresh = snapshot.Blocks.Where(block => block.IsFresh).Select(block => block.BlockId).Order().ToArray();
        CollectionAssert.AreEqual(new[] { "1", "GREEN-1", "GREEN-150", "RED-1", "RED-2", "RED-76" }, fresh);

        // A batch naming any block no territory owns is refused as a whole, as before.
        var unknown = MessageSerializer.CreateEnvelope("TrackModelBlockStates", new { blocks = new[] { new TrackModelBlockStateMessage { BlockId = "RED-3", ObservedAtUtc = now }, new TrackModelBlockStateMessage { BlockId = "BLUE-3", ObservedAtUtc = now } } });
        var refused = (WaysideAcknowledgementMessage)await router.ReceiveAsync(unknown);
        Assert.IsFalse(refused.IsAccepted);
        StringAssert.Contains(refused.Detail, "BLUE-3");
        Assert.IsFalse((await router.PollAsync()).Blocks.Single(block => block.BlockId == "RED-3").IsFresh);

        // Maintenance on one line closes that block only.
        var close = (WaysideAcknowledgementMessage)await router.ReceiveAsync(MessageSerializer.CreateEnvelope(nameof(MaintenanceRequestMessage), new MaintenanceRequestMessage { RequestId = "r", BlockId = "RED-2", RequestedState = MaintenanceState.Closed }));
        Assert.IsTrue(close.IsAccepted, close.Detail);
        snapshot = await router.PollAsync();
        CollectionAssert.AreEqual(new[] { "RED-2" }, snapshot.Blocks.Where(block => block.Maintenance != "Open").Select(block => block.BlockId).ToArray());
        StringAssert.Contains(router.GetOwnerDescription("RED-2"), "Red Line");
    }
}
