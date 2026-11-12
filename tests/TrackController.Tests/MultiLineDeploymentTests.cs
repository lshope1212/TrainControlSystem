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
        Assert.IsTrue(plan.Territories.GroupBy(territory => territory.Configuration.LineName).All(line => line.Count() == 2));
        StringAssert.StartsWith(plan.LayoutDescription, "3 lines");

        await using var router = plan.CreateRuntime();
        var snapshot = await router.PollAsync();
        Assert.HasCount(241, snapshot.Blocks);
        Assert.HasCount(241, snapshot.Blocks.Select(block => block.BlockId).Distinct().ToArray());
        Assert.AreEqual("Blue Line / Red Line / Green Line", snapshot.LineName);
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
