using System.Diagnostics;
using CTC.Core.Dispatching;
using CTC.Core.Models;
using TrackController.Contracts.Messages;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Integration.Tests;

/// <summary>
/// The production Track Model (all three course lines), the Track Controller with the combined
/// deployment (one territory per line) and CTC, live over real pipes. Every check below runs on
/// Blue, Red and Green; nothing is sent on a module's behalf except what the Train Model or a
/// dispatcher would send.
/// </summary>
[TestClass]
public sealed class ThreeLineIntegrationTests
{
    private static readonly string[] Lines = ["Blue", "Red", "Green"];

    /// <summary>A clear, unsignalled, switch-free block in the middle of each line.</summary>
    private static readonly Dictionary<string, string> Sample = new() { ["Blue"] = "12", ["Red"] = "RED-40", ["Green"] = "GREEN-40" };

    private static Task<OccupancyState?> CtcOccupancy(LiveSystem system, string blockId) =>
        system.OnCtc(ctc => ctc.State.FindBlock(blockId)?.Occupancy);

    private static Task<string[]> CtcOccupied(LiveSystem system) =>
        system.OnCtc(ctc => ctc.State.Lines.SelectMany(line => line.Blocks).Where(block => block.Occupancy == OccupancyState.Occupied).Select(block => block.BlockId).Order().ToArray());

    private static Task<bool> CtcHasLiveStatusForEveryBlock(LiveSystem system) => system.OnCtc(ctc =>
        ctc.State.Lines.Count == 3 && ctc.State.Lines.Sum(line => line.Blocks.Count) == 241
        && ctc.State.Lines.SelectMany(line => line.Blocks).All(block => !block.IsStatusStale && block.LiveStatusReceivedAtUtc is not null && block.Occupancy != OccupancyState.Unknown));

    /// <summary>All three modules started in the usual order; waits until every block of every line has a live status in CTC.</summary>
    private static async Task<LiveSystem> StartAllAsync(string? deployment = null)
    {
        var system = new LiveSystem();
        await system.StartTrackControllerAsync(deployment ?? LiveSystem.CourseDeployment);
        await system.StartCtcAsync();
        await system.StartTrackModelAsync(allLines: true);
        await LiveSystem.UntilAsync(() => CtcHasLiveStatusForEveryBlock(system), "CTC shows a live wayside status for all 241 blocks.", TimeSpan.FromSeconds(20));
        return system;
    }

    [TestMethod]
    public async Task AllThreeLines_ReachCtcFromTheTrackModel_WithLiveStatusForEveryBlock()
    {
        await using var system = await StartAllAsync();

        var lines = await system.OnCtc(ctc => ctc.State.Lines.Select(line => (line.LineId, line.Name, line.Blocks.Count)).ToArray());
        CollectionAssert.AreEqual(new[] { ("Blue", "Blue Line", 15), ("Red", "Red Line", 76), ("Green", "Green Line", 150) }, lines);
        Assert.IsTrue(system.CtcStatuses.Any(status => status.Contains("Track layout: added Blue, Red, Green", StringComparison.Ordinal)), string.Join(" | ", system.CtcStatuses));

        // Same IDs everywhere: the Track Controller reports exactly the Track Model's blocks.
        var trackIds = await system.OnTrackModel(track => track.Layout.Blocks.Select(block => block.Id).Order().ToArray());
        CollectionAssert.AreEqual(trackIds, system.Wayside!.LatestSnapshot.Blocks.Select(block => block.BlockId).Order().ToArray());
        Assert.IsTrue(system.Wayside.LatestSnapshot.Blocks.All(block => block.IsFresh), "Track Model feedback reaches the controller for every block.");

        // Only the Blue demonstration train is on the track.
        CollectionAssert.AreEqual(new[] { "1" }, await CtcOccupied(system));

        // The layout is not resent with live updates.
        int layouts = system.CtcStatuses.Count(status => status.StartsWith("Received Track layout", StringComparison.Ordinal));
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.AreEqual(layouts, system.CtcStatuses.Count(status => status.StartsWith("Received Track layout", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Occupancy_OnEachLine_PropagatesToTheRightBlockOnly_AndClears()
    {
        await using var system = await StartAllAsync();

        foreach (string line in Lines)
        {
            string block = Sample[line];
            string train = "T" + line;
            await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = train, CurrentBlockId = block });
            await LiveSystem.UntilAsync(() => Task.FromResult(system.WaysideBlock(block) is { IsFresh: true, Occupancy: OccupancyState.Occupied }), $"the Track Controller receives {block} occupied.");
            await LiveSystem.UntilAsync(async () => await CtcOccupancy(system, block) == OccupancyState.Occupied, $"CTC shows {block} occupied.");
            CollectionAssert.AreEqual(new[] { "1", block }.Order().ToArray(), await CtcOccupied(system), $"Only {block} changed; no other line or block was touched.");

            // Clearing removes the occupancy everywhere.
            await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = train, CurrentBlockId = "" });
            await LiveSystem.UntilAsync(async () => await CtcOccupancy(system, block) == OccupancyState.Clear, $"CTC shows {block} clear again.");
            CollectionAssert.AreEqual(new[] { "1" }, await CtcOccupied(system));
        }

        // Same block number on two lines: still two different blocks.
        await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = "TR", CurrentBlockId = "RED-12" });
        await LiveSystem.UntilAsync(async () => await CtcOccupancy(system, "RED-12") == OccupancyState.Occupied, "CTC shows RED-12 occupied.");
        Assert.AreEqual(OccupancyState.Clear, await CtcOccupancy(system, "12"));
        Assert.AreEqual(OccupancyState.Clear, await CtcOccupancy(system, "GREEN-12"));
    }

    [TestMethod]
    public async Task EquipmentAndFaults_OnRedAndGreen_PropagateAsMeasured()
    {
        await using var system = await StartAllAsync();

        // The PLC keeps every crossing closed while the controller is paused (fail-safe); running, a clear zone opens it.
        await system.ResumeTrackControllerAsync();
        string[] crossings = ["3", "RED-11", "RED-47", "GREEN-19", "GREEN-108"];
        await LiveSystem.UntilAsync(async () => await system.OnCtc(ctc => crossings.Skip(1).All(id => ctc.State.FindBlock(id)!.Crossing == CrossingState.Open)),
            "the running controller opens the Red and Green crossings whose zones are clear.");

        // A train next to each crossing: the controller closes it, the Track Model applies it, CTC shows the measurement.
        foreach (var (approach, crossing) in new[] { ("RED-10", "RED-11"), ("GREEN-18", "GREEN-19") })
        {
            await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = "X" + crossing, CurrentBlockId = approach });
            await LiveSystem.UntilAsync(async () => await system.OnTrackModel(track => track.FindBlock(crossing)!.Crossing) == CrossingState.Closed, $"the Track Controller closes crossing {crossing}.");
            await LiveSystem.UntilAsync(async () => await system.OnCtc(ctc => ctc.State.FindBlock(crossing)!.Crossing) == CrossingState.Closed, $"CTC shows crossing {crossing} closed.");
        }

        Assert.AreEqual(CrossingState.Open, await system.OnCtc(ctc => ctc.State.FindBlock("RED-47")!.Crossing), "The other Red crossing is unaffected.");
        Assert.AreEqual(CrossingState.Open, await system.OnCtc(ctc => ctc.State.FindBlock("GREEN-108")!.Crossing), "The other Green crossing is unaffected.");

        // Switches show the measured position.
        foreach (string id in new[] { "RED-16", "GREEN-13", "5" })
        {
            Assert.AreEqual(await system.OnTrackModel(track => track.FindBlock(id)!.Switch), await system.OnCtc(ctc => ctc.State.FindBlock(id)!.Switch), id);
        }

        // A failure is Unknown at that block only.
        await system.SendToTrackModelAsync(new TrackModelFailureCommandMessage { BlockId = "GREEN-50", TrackCircuitFailure = true });
        await LiveSystem.UntilAsync(async () => await CtcOccupancy(system, "GREEN-50") == OccupancyState.Unknown, "CTC shows GREEN-50 Unknown.");
        Assert.IsTrue(await system.OnCtc(ctc => AuthorityManager.IsUnsafe(ctc.State.FindBlock("GREEN-50")!)));
        Assert.AreEqual(1, await system.OnCtc(ctc => ctc.State.Lines.SelectMany(line => line.Blocks).Count(block => block.Occupancy == OccupancyState.Unknown)));
        Assert.AreEqual(OccupancyState.Clear, await CtcOccupancy(system, "RED-50"));
    }

    [TestMethod]
    public async Task UnknownBlockIds_AreRejected_NeverMapped()
    {
        await using var system = await StartAllAsync();

        // CTC: a status for a block no line has.
        await system.SendToCtcAsync(new BlockStatusMessage { BlockId = "RED-77", Occupancy = OccupancyState.Occupied });
        await LiveSystem.UntilAsync(() => Task.FromResult(system.CtcStatuses.Any(status => status.StartsWith("Rejected Block status", StringComparison.Ordinal) && status.Contains("RED-77"))), "CTC rejects the unknown block.");
        CollectionAssert.AreEqual(new[] { "1" }, await CtcOccupied(system));

        // Track Controller: feedback and requests for blocks it does not own.
        await system.SendToTrackControllerAsync(new TrackModelBlockStateMessage { BlockId = "GREEN-151", Occupancy = OccupancyState.Occupied });
        await system.SendToTrackControllerAsync(new MaintenanceRequestMessage { RequestId = "unknown-block", BlockId = "77", RequestedState = MaintenanceState.Closed });
        await LiveSystem.UntilAsync(() => Task.FromResult(system.WaysideReceipts.Count(receipt => !receipt.IsAccepted && receipt.Detail.Contains("has no owner")) >= 2), "the Track Controller rejects both.");
        Assert.HasCount(241, system.Wayside!.LatestSnapshot.Blocks);

        // Track Model: a train cannot be placed on a block that does not exist.
        await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = "ghost", CurrentBlockId = "GREEN-0" });
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        CollectionAssert.AreEqual(new[] { "1" }, await CtcOccupied(system));
    }

    [TestMethod]
    public async Task LostAndRestoredModules_AreConservative_AndRecoverWithoutRestartingTheOthers()
    {
        await using var system = await StartAllAsync();

        // Track Model stops: every block on every line goes stale at the controller and Unknown in CTC.
        await system.StopTrackModelAsync();
        await LiveSystem.UntilAsync(() => Task.FromResult(system.Wayside!.LatestSnapshot.Blocks.All(block => !block.IsFresh)), "the controller's feedback goes stale.", TimeSpan.FromSeconds(10));
        await LiveSystem.UntilAsync(async () => await system.OnCtc(ctc => ctc.State.Lines.SelectMany(line => line.Blocks).All(block => block.Occupancy == OccupancyState.Unknown && AuthorityManager.IsUnsafe(block))),
            "CTC shows Unknown, never a fabricated value.");

        // It comes back: live statuses resume on all lines, nothing else restarted.
        await system.StartTrackModelAsync(allLines: true);
        await LiveSystem.UntilAsync(() => CtcHasLiveStatusForEveryBlock(system), "live statuses resume after the Track Model restarts.", TimeSpan.FromSeconds(15));

        // Track Controller stops: CTC stops trusting every line; a new controller restores them.
        await system.StopTrackControllerAsync();
        await LiveSystem.UntilAsync(async () => await system.OnCtc(ctc => ctc.State.Lines.SelectMany(line => line.Blocks).All(block => block.IsStatusStale && block.Occupancy == OccupancyState.Unknown)),
            "CTC marks every line stale.", TimeSpan.FromSeconds(8));
        await system.StartTrackControllerAsync(LiveSystem.CourseDeployment);
        await LiveSystem.UntilAsync(() => CtcHasLiveStatusForEveryBlock(system), "live statuses resume after the Track Controller restarts.", TimeSpan.FromSeconds(15));
    }

    [TestMethod]
    public async Task RestartedCtc_ResynchronizesAllLines_AndARepeatedLayoutKeepsLiveState()
    {
        await using var system = await StartAllAsync();

        // An equivalent layout (forced by a snapshot request) leaves CTC's live state alone.
        await system.OnCtcAsync(ctc => ctc.CloseBlockAsync("RED-40"));
        await LiveSystem.UntilAsync(async () => await system.OnCtc(ctc => ctc.State.FindBlock("RED-40")!.ConfirmedMaintenanceState) == MaintenanceState.Closed, "RED-40 confirmed closed.");
        await system.SendToTrackModelAsync(new TrackModelSnapshotRequestMessage());
        await LiveSystem.UntilAsync(() => Task.FromResult(system.CtcStatuses.Any(status => status.Contains("unchanged", StringComparison.OrdinalIgnoreCase))), "CTC receives the repeated layout.");
        Assert.AreEqual(MaintenanceState.Closed, await system.OnCtc(ctc => ctc.State.FindBlock("RED-40")!.ConfirmedMaintenanceState));
        Assert.IsTrue(await CtcHasLiveStatusForEveryBlock(system), "No block was reset to Unknown.");

        // A new CTC instance gets the whole network again without the TestUI.
        await system.StopCtcAsync();
        await system.StartCtcAsync();
        await LiveSystem.UntilAsync(() => CtcHasLiveStatusForEveryBlock(system), "a restarted CTC loads all three lines and their live statuses.", TimeSpan.FromSeconds(15));
        Assert.AreEqual(MaintenanceState.Closed, await system.OnCtc(ctc => ctc.State.FindBlock("RED-40")!.ConfirmedMaintenanceState), "The closure is the controller's state and is reported again.");
    }

    [TestMethod]
    public async Task Maintenance_CloseAndReopenOnEachLine_IsAcknowledgedAndConfirmed_WithoutCrossTalk()
    {
        await using var system = await StartAllAsync();

        foreach (string line in Lines)
        {
            string block = Sample[line];
            int acknowledged = system.CtcStatuses.Count(status => status.StartsWith("Received Track Controller acknowledgement", StringComparison.Ordinal));
            await system.OnCtcAsync(ctc => ctc.CloseBlockAsync(block));
            await LiveSystem.UntilAsync(() => Task.FromResult(system.CtcStatuses.Count(status => status.StartsWith("Received Track Controller acknowledgement", StringComparison.Ordinal)) > acknowledged),
                $"the Track Controller acknowledges the closure of {block}.");
            await LiveSystem.UntilAsync(async () => await system.OnCtc(ctc => ctc.State.FindBlock(block)!.ConfirmedMaintenanceState) == MaintenanceState.Closed, $"{block} confirmed closed.");
            Assert.AreEqual(1, await system.OnCtc(ctc => ctc.State.Lines.SelectMany(l => l.Blocks).Count(b => b.ConfirmedMaintenanceState == MaintenanceState.Closed)), "Only the requested block on the requested line closes.");

            await system.OnCtcAsync(ctc => ctc.ReopenBlockAsync(block));
            await LiveSystem.UntilAsync(async () => await system.OnCtc(ctc => ctc.State.FindBlock(block)!.ConfirmedMaintenanceState) == MaintenanceState.Open, $"{block} confirmed reopened.");
        }

        // Closures on all three lines at once do not interfere.
        foreach (string block in Sample.Values)
        {
            await system.OnCtcAsync(ctc => ctc.CloseBlockAsync(block));
        }

        await LiveSystem.UntilAsync(async () => await system.OnCtc(ctc => Sample.Values.All(id => ctc.State.FindBlock(id)!.ConfirmedMaintenanceState == MaintenanceState.Closed)), "all three closures confirmed.");
        Assert.AreEqual(MaintenanceState.Open, await system.OnCtc(ctc => ctc.State.FindBlock("RED-12")!.ConfirmedMaintenanceState));
        Assert.AreEqual(MaintenanceState.Open, await system.OnCtc(ctc => ctc.State.FindBlock("GREEN-12")!.ConfirmedMaintenanceState));
    }

    [TestMethod]
    public async Task Maintenance_OnOccupiedBlocks_StaysPendingUntilClear()
    {
        await using var system = await StartAllAsync();

        foreach (string block in new[] { "RED-60", "GREEN-60" })
        {
            await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = "P" + block, CurrentBlockId = block });
            await LiveSystem.UntilAsync(async () => await CtcOccupancy(system, block) == OccupancyState.Occupied, $"{block} occupied.");
            await system.OnCtcAsync(ctc => ctc.CloseBlockAsync(block));
            await LiveSystem.UntilAsync(() => Task.FromResult(system.WaysideBlock(block).Maintenance == "Pending closure"), $"the controller holds {block} pending closure.");
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            var held = await system.OnCtc(ctc => ctc.State.FindBlock(block)!);
            Assert.AreEqual(MaintenanceState.Open, held.ConfirmedMaintenanceState, "Accepted is not closed: the block is still occupied.");
            Assert.AreEqual(MaintenanceState.Closed, held.RequestedMaintenanceState);

            await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = "P" + block, CurrentBlockId = "" });
            await LiveSystem.UntilAsync(async () => await system.OnCtc(ctc => ctc.State.FindBlock(block)!.ConfirmedMaintenanceState) == MaintenanceState.Closed, $"{block} closes once it clears.");
        }
    }

    /// <summary>
    /// A train scheduled on Red or Green is released by the shared clock's time, placed through the
    /// Track Model and detected by CTC, which sends a complete request the line's territory evaluates.
    /// </summary>
    private static async Task<LiveSystem> PlacedTrainAsync(string line, string[] route, string? deployment = null)
    {
        var departure = new TimeSpan(9, 0, 30);
        var system = await StartAllAsync(deployment);
        await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = "01", CurrentBlockId = "" });
        await LiveSystem.UntilAsync(async () => (await CtcOccupied(system)).Length == 0, "every line reports clear.");

        var train = new ScheduledTrain { TrainId = "000", LineId = line };
        for (int i = 0; i < route.Length; i++)
        {
            train.Route.Add(new ScheduledRouteBlock { BlockId = route[i], ArrivalTime = i == 0 || i == route.Length - 1 ? departure + TimeSpan.FromSeconds(30 * i) : null });
        }

        await system.OnCtc(ctc =>
        {
            ctc.QueueSchedule([train]);
            return 0;
        });
        await system.SendToCtcAsync(new SystemTimeMessage { SystemTime = departure, SpeedMultiplier = 1, IsPaused = true });
        await LiveSystem.UntilAsync(() => system.OnCtc(ctc => ctc.State.FindDispatchedTrain("000")?.ReleaseStatus == TrainReleaseStatus.AwaitingPlacement), "the train is released, awaiting placement.");
        await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = "000", CurrentBlockId = route[0] });
        await LiveSystem.UntilAsync(() => system.OnCtc(ctc => ctc.State.FindDispatchedTrain("000")!.MovementRequest is { Delivery: MovementRequestDelivery.Delivered }), "CTC detects the train and sends its request.");
        return system;
    }

    private static Task<MovementRequestState?> Request(LiveSystem system) => system.OnCtc(ctc => ctc.State.FindDispatchedTrain("000")!.MovementRequest);

    [TestMethod]
    [DataRow("Red", new[] { "RED-20", "RED-21", "RED-22", "RED-23", "RED-24", "RED-25" })]
    [DataRow("Green", new[] { "GREEN-63", "GREEN-64", "GREEN-65", "GREEN-66", "GREEN-67", "GREEN-68" })]
    public async Task MovementOnRedAndGreen_IsEvaluatedByThatLinesTerritory_AndTheActualAuthorizationReachesCtc(string line, string[] route)
    {
        await using var system = await PlacedTrainAsync(line, route);
        await LiveSystem.UntilAsync(async () => (await Request(system))?.Acknowledgement == RequestAcknowledgementOutcome.AcceptedForEvaluation, "the line's territory accepts the request.");
        var request = (await Request(system))!;
        Assert.AreEqual(route[0], request.Plan.TargetBlockId);
        CollectionAssert.AreEqual(route.Skip(1).Take(request.Plan.PathBlockIds.Count).ToArray(), request.Plan.PathBlockIds.ToArray());
        StringAssert.Contains(system.Wayside!.GetOwnerDescription(route[0]), line + " Line");

        // The controller holds this request on that block only.
        Assert.AreEqual(request.RequestId, system.WaysideBlock(route[0]).Request!.RequestId);
        Assert.HasCount(1, system.Wayside.LatestSnapshot.Authorizations);

        // Paused: accepted, not authorized. Resumed: the real decision, equal to what reached the track.
        Assert.IsFalse(request.IsMovementAuthorized);
        await system.ResumeTrackControllerAsync();
        await LiveSystem.UntilAsync(async () => (await Request(system))!.IsMovementAuthorized, "the territory grants the movement.");
        var granted = (await Request(system))!;
        Assert.IsGreaterThan(0.0, granted.AuthorizedAuthorityMeters);
        Assert.IsLessThanOrEqualTo(granted.Plan.AuthorityMeters, granted.AuthorizedAuthorityMeters);
        Assert.IsLessThanOrEqualTo(granted.Plan.SpeedMetersPerSecond + 1e-9, granted.AuthorizedSpeedMetersPerSecond, "Never faster than CTC suggested.");
        double limit = await system.OnTrackModel(track => route.Take(1 + granted.Plan.PathBlockIds.Count).Min(id => track.FindBlock(id)!.SpeedLimitMetersPerSecond));
        Assert.IsLessThanOrEqualTo(limit + 1e-9, granted.AuthorizedSpeedMetersPerSecond, "Never above the line's civil speed limits.");
        await LiveSystem.UntilAsync(async () => Math.Abs(await system.OnTrackModel(track => track.FindBlock(route[0])!.AuthorityMeters) - granted.AuthorizedAuthorityMeters) < 1e-6,
            "CTC reports exactly the authority commanded to the track.");

        // The train moves on: a new request from the next block, on the same line.
        await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = "000", CurrentBlockId = route[1] });
        await LiveSystem.UntilAsync(async () => (await Request(system))?.Plan.TargetBlockId == route[1], "CTC re-targets the request.");
        await LiveSystem.UntilAsync(async () => (await Request(system))?.Acknowledgement == RequestAcknowledgementOutcome.AcceptedForEvaluation, "the new request is accepted.");
        Assert.AreEqual(route[0], (await Request(system))!.Plan.EntryBlockId);
    }

    [TestMethod]
    public async Task GreenAgainstTheDirectionOfTravel_IsRejected()
    {
        // K (63-68) runs toward increasing numbers; this schedule runs it backwards.
        await using var system = await PlacedTrainAsync("Green", ["GREEN-68", "GREEN-67", "GREEN-66", "GREEN-65"]);
        await LiveSystem.UntilAsync(async () => (await Request(system))?.Acknowledgement == RequestAcknowledgementOutcome.Rejected, "the Green territory rejects the request.");
        var request = (await Request(system))!;
        StringAssert.Contains(request.AcknowledgementDetail, "No permitted connection from GREEN-68 to GREEN-67");
        Assert.IsFalse(request.IsMovementAuthorized);
        Assert.IsNull(system.WaysideBlock("GREEN-68").Request);
    }

    [TestMethod]
    public async Task InvalidMovementRequests_AreRejected_AndStaleFeedbackWithholdsAuthority()
    {
        await using var system = await StartAllAsync();
        await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = "T", CurrentBlockId = "RED-20" });
        await LiveSystem.UntilAsync(() => Task.FromResult(system.WaysideBlock("RED-20").Occupancy == OccupancyState.Occupied), "RED-20 occupied at the controller.");

        MovementSuggestionMessage Movement(string id, string target, params string[] path) => new()
        {
            RequestId = id, TrainId = "000", TargetBlockId = target, PathBlockIds = [.. path],
            SuggestedSpeedMetersPerSecond = 10, SuggestedAuthorityMeters = 200,
        };

        await system.SendToTrackControllerAsync(Movement("unknown", "RED-99", "RED-100"));
        await system.SendToTrackControllerAsync(Movement("gap", "RED-20", "RED-22"));
        await system.SendToTrackControllerAsync(Movement("other-line", "RED-20", "RED-21", "GREEN-22"));
        await LiveSystem.UntilAsync(() => Task.FromResult(system.WaysideReceipts.Count(receipt => !receipt.IsAccepted && receipt.MessageType == nameof(MovementSuggestionMessage)) >= 3),
            "the controller rejects all three.");
        var reasons = system.WaysideReceipts.Where(receipt => !receipt.IsAccepted && receipt.MessageType == nameof(MovementSuggestionMessage)).Select(receipt => receipt.Detail).ToArray();
        Assert.IsTrue(reasons.Any(reason => reason.Contains("'RED-99' has no owner")), string.Join(" | ", reasons));
        Assert.IsTrue(reasons.Any(reason => reason.Contains("No permitted connection from RED-20 to RED-22")), string.Join(" | ", reasons));
        Assert.IsTrue(reasons.Any(reason => reason.Contains("territory boundary")), "A route onto another line is another territory and is refused.");
        Assert.IsEmpty(system.Wayside!.LatestSnapshot.Authorizations);

        // A valid request, then the Track Model stops: the controller withholds rather than trusting stale data.
        await system.ResumeTrackControllerAsync();
        await system.SendToTrackControllerAsync(Movement("valid", "RED-20", "RED-21", "RED-22"));
        await LiveSystem.UntilAsync(() => Task.FromResult(system.WaysideBlock("RED-20").AuthorizedAuthorityMeters > 0), "the valid request is authorized.");
        await system.StopTrackModelAsync();
        await LiveSystem.UntilAsync(() => Task.FromResult(system.WaysideBlock("RED-20") is { AuthorizedAuthorityMeters: 0, MovementStatus: "Withheld" }), "stale feedback withholds the authority.", TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task GreenSplitIntoTwoTerritories_AllowsMovementInsideEach_AndRejectsCrossingTheBoundary()
    {
        // A test-only split of Green: G-M (blocks 30-75) gets its own territory; production uses one
        // territory per line. The cut keeps every switch and crossing zone whole, as the loader requires.
        await using var probe = new LiveSystem();
        string folder = probe.CopySamples(path =>
        {
            LiveSystem.EditJson(Path.Combine(path, "green-line-territories-single.json"), json =>
            {
                var all = json["territories"]![0]!["blockIds"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();
                bool InGtoM(string id) => int.Parse(id["GREEN-".Length..]) is >= 30 and <= 75;
                System.Text.Json.Nodes.JsonArray Ids(IEnumerable<string> ids) => new([.. ids.Select(id => (System.Text.Json.Nodes.JsonNode)id)]);
                json["territories"] = new System.Text.Json.Nodes.JsonArray(
                    new System.Text.Json.Nodes.JsonObject { ["id"] = "wayside-green-main", ["name"] = "Green main", ["blockIds"] = Ids(all.Where(id => !InGtoM(id))) },
                    new System.Text.Json.Nodes.JsonObject { ["id"] = "wayside-green-gm", ["name"] = "Green G-M", ["blockIds"] = Ids(all.Where(InGtoM)) });
            });
            LiveSystem.EditJson(Path.Combine(path, "course-all-pc.deployment.json"), json =>
            {
                var territories = json["territories"]!.AsArray();
                territories.RemoveAt(2);
                territories.Add(new System.Text.Json.Nodes.JsonObject { ["territoryId"] = "wayside-green-main", ["execution"] = "Local" });
                territories.Add(new System.Text.Json.Nodes.JsonObject { ["territoryId"] = "wayside-green-gm", ["execution"] = "Local" });
            });
        });

        // Valid on the track (one-way M into N), but it leaves G-M at 75 -> 76.
        await using var crossing = await PlacedTrainAsync("Green", ["GREEN-73", "GREEN-74", "GREEN-75", "GREEN-76", "GREEN-77"], Path.Combine(folder, "course-all-pc.deployment.json"));
        await LiveSystem.UntilAsync(async () => (await Request(crossing))?.Acknowledgement == RequestAcknowledgementOutcome.Rejected, "a request across the boundary is rejected.");
        StringAssert.Contains((await Request(crossing))!.AcknowledgementDetail, "territory boundary");
        Assert.IsFalse((await Request(crossing))!.IsMovementAuthorized);
        Assert.IsNull(crossing.WaysideBlock("GREEN-73").Request);

        // Inside one territory the same split accepts movement.
        await crossing.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = "inside", CurrentBlockId = "GREEN-60" });
        await LiveSystem.UntilAsync(() => Task.FromResult(crossing.WaysideBlock("GREEN-60").Occupancy == OccupancyState.Occupied), "GREEN-60 occupied.");
        await crossing.SendToTrackControllerAsync(new MovementSuggestionMessage
        {
            RequestId = "inside", TrainId = "inside", TargetBlockId = "GREEN-60", EntryBlockId = "GREEN-59", PathBlockIds = ["GREEN-61", "GREEN-62"],
            SuggestedSpeedMetersPerSecond = 8, SuggestedAuthorityMeters = 150,
        });
        await LiveSystem.UntilAsync(() => Task.FromResult(crossing.WaysideReceipts.Any(receipt => receipt.IsAccepted && receipt.MessageType == nameof(MovementSuggestionMessage))), "the G-M territory accepts a request inside it.");
        Assert.AreEqual("inside", crossing.WaysideBlock("GREEN-60").Request!.RequestId);
        StringAssert.Contains(crossing.Wayside!.GetOwnerDescription("GREEN-60"), "Green G-M");
    }

    /// <summary>
    /// Continuous publishing for the full network: CTC's statuses stay current (no backlog), a
    /// change on any line reaches CTC quickly, and acknowledgements are not held up by status traffic.
    /// </summary>
    [TestMethod]
    public async Task ContinuousThreeLineUpdates_StayCurrent_AndAcknowledgementsAreNotStarved()
    {
        await using var system = await StartAllAsync();
        await Task.Delay(TimeSpan.FromSeconds(4));

        // No backlog: every block's latest status in CTC is a recent evaluation.
        var oldest = await system.OnCtc(ctc => ctc.State.Lines.SelectMany(line => line.Blocks).Min(block => block.StatusReportedAtUtc!.Value));
        double statusAge = (DateTimeOffset.UtcNow - oldest).TotalSeconds;
        Assert.IsLessThan(1.5, statusAge, "CTC applies the controller's evaluations as they are made.");

        // End to end, Track Model -> Track Controller -> CTC, on each line.
        var latencies = new List<double>();
        foreach (string line in Lines)
        {
            string block = Sample[line];
            var clock = Stopwatch.StartNew();
            await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = "L" + line, CurrentBlockId = block });
            await LiveSystem.UntilAsync(async () => await CtcOccupancy(system, block) == OccupancyState.Occupied, $"{block} reaches CTC.");
            latencies.Add(clock.Elapsed.TotalSeconds);
        }

        Assert.IsTrue(latencies.All(seconds => seconds < 2.0), "Occupancy latency per line (s): " + string.Join(", ", latencies.Select(seconds => seconds.ToString("0.00"))));

        // Requests on all three lines at once are answered promptly despite the status traffic.
        int before = system.CtcStatuses.Count(status => status.StartsWith("Received Track Controller acknowledgement", StringComparison.Ordinal));
        var sent = Stopwatch.StartNew();
        foreach (string block in new[] { "13", "RED-70", "GREEN-140" })
        {
            await system.OnCtcAsync(ctc => ctc.CloseBlockAsync(block));
        }

        await LiveSystem.UntilAsync(() => Task.FromResult(system.CtcStatuses.Count(status => status.StartsWith("Received Track Controller acknowledgement", StringComparison.Ordinal)) >= before + 3),
            "all three acknowledgements arrive.");
        Assert.IsLessThan(1.5, sent.Elapsed.TotalSeconds, "Acknowledgements are sent ahead of the periodic status batch.");
        Console.WriteLine($"Oldest block status at CTC: {statusAge:0.00} s old; " +
            $"occupancy latency Blue/Red/Green: {string.Join(" / ", latencies.Select(seconds => seconds.ToString("0.00")))} s; " +
            $"three closures sent and acknowledged in {sent.Elapsed.TotalSeconds:0.00} s.");
    }

    [TestMethod]
    public async Task OfflineCtc_DoesNotHoldUpTheTrackModelAndTrackController()
    {
        await using var system = await StartAllAsync();
        await system.StopCtcAsync();

        // Well past the controller's 5 s watchdog: Track Model -> Track Controller stays live on every line.
        await Task.Delay(TimeSpan.FromSeconds(7));
        Assert.IsTrue(system.Wayside!.LatestSnapshot.Blocks.All(block => block.IsFresh));

        // And the controller's commands still reach the Track Model (a crossing closes for a train next to it).
        await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = "X", CurrentBlockId = "GREEN-107" });
        await LiveSystem.UntilAsync(async () => await system.OnTrackModel(track => track.FindBlock("GREEN-108")!.Crossing) == CrossingState.Closed,
            "the Track Model still receives the controller's commands.");
    }
}
