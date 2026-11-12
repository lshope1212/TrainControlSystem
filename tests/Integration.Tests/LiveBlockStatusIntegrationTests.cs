using CTC.Core.Dispatching;
using CTC.TestUI.Wpf.SampleData;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Integration.Tests;

/// <summary>
/// Live block-status path with the real modules: Track Model -> Track Controller -> CTC, plus the
/// layout path Track Model -> CTC. No TestUI and no fake module is involved.
/// </summary>
[TestClass]
public sealed class LiveBlockStatusIntegrationTests
{
    private static Task<bool> CtcHasBlueLine(LiveSystem system) =>
        system.OnCtc(ctc => ctc.State.FindLine("Blue") is { Blocks.Count: 15 });

    private static Task<OccupancyState?> CtcOccupancy(LiveSystem system, string blockId) =>
        system.OnCtc(ctc => ctc.State.FindBlock(blockId)?.Occupancy);

    /// <summary>Starts all three modules in the usual order and waits for live statuses to reach CTC.</summary>
    private static async Task<LiveSystem> StartAllAsync()
    {
        var system = new LiveSystem();
        await system.StartTrackControllerAsync();
        await system.StartCtcAsync();
        await system.StartTrackModelAsync(demonstrationTrain: true);
        await LiveSystem.UntilAsync(() => CtcHasBlueLine(system), "CTC loads the Track Model's Blue Line layout.");

        // The demonstration places train 001 in block 1; everything else is clear.
        await LiveSystem.UntilAsync(async () => await CtcOccupancy(system, "1") == OccupancyState.Occupied && await CtcOccupancy(system, "15") == OccupancyState.Clear,
            "the Track Model's measured occupancy reaches CTC through the Track Controller.");
        return system;
    }

    [TestMethod]
    public async Task RealTrackModelLayout_LoadsIntoCtc_WithoutTheTestUi()
    {
        await using var system = await StartAllAsync();

        var line = await system.OnCtc(ctc => ctc.State.FindLine("Blue")!);
        Assert.AreEqual("Blue Line", line.Name);
        var block5 = await system.OnCtc(ctc => ctc.State.FindBlock("5")!);
        Assert.IsTrue(block5.HasSwitch);
        Assert.AreEqual("A", block5.Section);
        Assert.AreEqual(50.0, block5.SpeedLimitKilometersPerHour, 1e-6);

        // The layout is not resent with every occupancy update or heartbeat.
        int layoutsAfterLoad = system.CtcStatuses.Count(status => status.StartsWith("Received Track layout", StringComparison.Ordinal));
        await Task.Delay(TimeSpan.FromSeconds(3));
        int layoutsLater = system.CtcStatuses.Count(status => status.StartsWith("Received Track layout", StringComparison.Ordinal));
        Assert.IsLessThanOrEqualTo(2, layoutsAfterLoad);
        Assert.AreEqual(layoutsAfterLoad, layoutsLater);
    }

    [TestMethod]
    public async Task CtcStartedAfterTrackModel_StillSynchronizesTheLayout()
    {
        await using var system = new LiveSystem();
        await system.StartTrackControllerAsync();
        await system.StartTrackModelAsync(demonstrationTrain: true);

        // The Track Model's initial layout delivery to CTC fails: CTC is not running yet.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await system.StartCtcAsync();

        await LiveSystem.UntilAsync(() => CtcHasBlueLine(system), "a late CTC still obtains the production layout.");
        await LiveSystem.UntilAsync(async () => await CtcOccupancy(system, "1") == OccupancyState.Occupied, "live status follows.");
    }

    [TestMethod]
    public async Task TrackModelOccupancyChange_ReachesTrackControllerThenCtc_AndClearingFollows()
    {
        await using var system = await StartAllAsync();

        // A Train Model telemetry input to the production Track Model moves train 001 from block 1 to block 2.
        await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = "001", CurrentBlockId = "2" });

        await LiveSystem.UntilAsync(() => Task.FromResult(system.WaysideBlock("2").Occupancy == OccupancyState.Occupied && system.WaysideBlock("2").IsFresh),
            "the Track Controller receives the Track Model's measurement.");
        await LiveSystem.UntilAsync(async () => await CtcOccupancy(system, "2") == OccupancyState.Occupied && await CtcOccupancy(system, "1") == OccupancyState.Clear,
            "CTC shows block 2 occupied and block 1 clear.");

        // Removing the train clears the block everywhere.
        await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = "001", CurrentBlockId = "" });
        await LiveSystem.UntilAsync(async () => await CtcOccupancy(system, "2") == OccupancyState.Clear, "CTC shows block 2 clear again.");
    }

    [TestMethod]
    public async Task EquipmentAndFaults_PropagateAsMeasured_NeverAsRequested()
    {
        await using var system = await StartAllAsync();

        // A train in the crossing's zone: the Track Controller commands the crossing closed, the Track
        // Model applies it, and CTC shows what the Track Model measures.
        await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = "001", CurrentBlockId = "3" });
        await LiveSystem.UntilAsync(async () => await system.OnTrackModel(track => track.FindBlock("3")!.Crossing) == CrossingState.Closed,
            "the Track Controller closes the crossing in the Track Model.");
        await LiveSystem.UntilAsync(async () => await system.OnCtc(ctc => ctc.State.FindBlock("3")!.Crossing) == CrossingState.Closed,
            "CTC shows the measured crossing state.");

        // A track-circuit failure makes occupancy Unknown end to end; nobody substitutes a value.
        await system.SendToTrackModelAsync(new TrackModelFailureCommandMessage { BlockId = "7", TrackCircuitFailure = true });
        await LiveSystem.UntilAsync(async () => await CtcOccupancy(system, "7") == OccupancyState.Unknown, "CTC shows the failed track circuit as Unknown.");
        Assert.IsTrue(await system.OnCtc(ctc => AuthorityManager.IsUnsafe(ctc.State.FindBlock("7")!)));

        // A switch REQUEST from CTC is answered by the Track Controller, but the displayed switch
        // only ever shows what the Track Model measures.
        var measuredBefore = await system.OnTrackModel(track => track.FindBlock("5")!.Switch);
        await LiveSystem.UntilAsync(async () => await system.OnCtc(ctc => ctc.State.FindBlock("5")!.Switch) == measuredBefore, "CTC shows the measured switch.");
        var requested = measuredBefore == SwitchPosition.Normal ? SwitchPosition.Reverse : SwitchPosition.Normal;
        await system.OnCtcAsync(ctc => ctc.SetSwitchPositionAsync("5", requested));

        await LiveSystem.UntilAsync(() => Task.FromResult(system.CtcStatuses.Any(status => status.StartsWith("Received Track Controller acknowledgement", StringComparison.Ordinal))),
            "the Track Controller acknowledges the switch request.");
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var measuredNow = await system.OnTrackModel(track => track.FindBlock("5")!.Switch);
        await LiveSystem.UntilAsync(async () => await system.OnCtc(ctc => ctc.State.FindBlock("5")!.Switch) == measuredNow,
            "CTC's switch is the measured position, whether or not the request was carried out.");
    }

    [TestMethod]
    public async Task HealthyUnchangedFeedback_StaysFreshPastTheWatchdog()
    {
        await using var system = await StartAllAsync();

        // Nothing changes for longer than the Track Controller's 5 s feedback watchdog.
        await Task.Delay(TimeSpan.FromSeconds(7));

        Assert.IsTrue(system.Wayside!.LatestSnapshot.Blocks.All(block => block.IsFresh), "The heartbeat keeps unchanged measurements fresh.");
        Assert.IsFalse(await system.OnCtc(ctc => ctc.State.Lines.SelectMany(line => line.Blocks).Any(block => block.IsStatusStale || block.Occupancy == OccupancyState.Unknown)));
        Assert.AreEqual(OccupancyState.Occupied, await CtcOccupancy(system, "1"));
    }

    [TestMethod]
    public async Task DisconnectedTrackModel_MakesFeedbackSafelyStale()
    {
        await using var system = await StartAllAsync();

        await system.StopTrackModelAsync();

        await LiveSystem.UntilAsync(() => Task.FromResult(system.Wayside!.LatestSnapshot.Blocks.All(block => !block.IsFresh)),
            "the Track Controller's feedback goes stale without the Track Model.", TimeSpan.FromSeconds(10));
        await LiveSystem.UntilAsync(async () => await system.OnCtc(ctc => ctc.State.Lines.SelectMany(line => line.Blocks).All(block => block.Occupancy == OccupancyState.Unknown)),
            "CTC receives Unknown, never a fabricated healthy value.");
        Assert.IsTrue(await system.OnCtc(ctc => ctc.State.Lines.SelectMany(line => line.Blocks).All(AuthorityManager.IsUnsafe)));
    }

    [TestMethod]
    public async Task DisconnectedTrackController_MakesCtcStatusStale()
    {
        await using var system = await StartAllAsync();

        await system.StopTrackControllerAsync();

        await LiveSystem.UntilAsync(async () => await system.OnCtc(ctc => ctc.State.Lines.SelectMany(line => line.Blocks).All(block => block.IsStatusStale && block.Occupancy == OccupancyState.Unknown)),
            "CTC stops trusting statuses that are no longer refreshed.", TimeSpan.FromSeconds(8));
    }

    [TestMethod]
    public async Task MaintenanceClose_IsAcknowledgedAndConfirmedByTheRealTrackController()
    {
        await using var system = await StartAllAsync();

        await system.OnCtcAsync(ctc => ctc.CloseBlockAsync("12"));

        await LiveSystem.UntilAsync(async () => await system.OnCtc(ctc => ctc.State.FindBlock("12")!.ConfirmedMaintenanceState) == MaintenanceState.Closed,
            "the Track Controller confirms the closure in its block status.");
        Assert.IsTrue(system.CtcStatuses.Any(status => status.StartsWith("Received Track Controller acknowledgement", StringComparison.Ordinal)));

        await system.OnCtcAsync(ctc => ctc.ReopenBlockAsync("12"));
        await LiveSystem.UntilAsync(async () => await system.OnCtc(ctc => ctc.State.FindBlock("12")!.ConfirmedMaintenanceState) == MaintenanceState.Open,
            "the Track Controller confirms the reopening.");
    }

    [TestMethod]
    public async Task CtcAlone_StillWorksWithTestUiInputs()
    {
        // No real Track Model or Track Controller: the CTC TestUI's isolated workflow still works.
        await using var system = new LiveSystem();
        await system.StartCtcAsync();

        await system.SendToCtcAsync(SampleTrackLayout.Create());
        await LiveSystem.UntilAsync(() => system.OnCtc(ctc => ctc.State.FindLine("BLUE") is not null), "CTC loads the TestUI sample layout.");
        await system.SendToCtcAsync(new BlockStatusMessage { BlockId = "A1", Occupancy = OccupancyState.Clear });
        await LiveSystem.UntilAsync(async () => await system.OnCtc(ctc => ctc.State.FindBlock("A1")!.Occupancy) == OccupancyState.Clear, "the TestUI status applies.");

        // A TestUI status is not periodic, so it never goes stale.
        await Task.Delay(TimeSpan.FromSeconds(4.5));
        Assert.AreEqual(OccupancyState.Clear, await system.OnCtc(ctc => ctc.State.FindBlock("A1")!.Occupancy));
        Assert.IsFalse(await system.OnCtc(ctc => ctc.State.FindBlock("A1")!.IsStatusStale));
    }
}
