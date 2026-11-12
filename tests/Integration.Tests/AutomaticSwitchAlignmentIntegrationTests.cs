using TrackModel.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Integration.Tests;

/// <summary>
/// Automatic switch alignment across the real modules: CTC plans and requests the movement, the Track
/// Controller (bundled PLC) derives the switch positions from the requested route, checks each switch's
/// protection zone and commands it, the Track Model throws it and reports it, and authority follows only
/// the reported alignment. No test commands a switch or puts one where its route needs it.
/// </summary>
/// <remarks>
/// The Normal-to-Reverse acceptance case (Station C from a Normal switch) and the already-aligned case
/// (Station B) are in <see cref="SignalledRouteIntegrationTests"/>.
/// </remarks>
[TestClass]
public sealed class AutomaticSwitchAlignmentIntegrationTests
{
    private static string[] ToStationB => SignalledRouteIntegrationTests.ToStationB;
    private static string[] ToStationC => SignalledRouteIntegrationTests.ToStationC;

    /// <summary>Test C: switch 5 starts Reverse; a Station B route throws it Normal, waits for the report, and the train takes the Normal branch.</summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task SwitchLyingReverse_StationBRoute_ThrowsItNormal_ConfirmsIt_AndTheTrainTakesTheNormalBranch()
    {
        // The Track Model's start-up state has the switch on the wrong leg for this route.
        await using var run = await DispatchedRun.StartAsync("Blue", ToStationB, track => track.FindBlock("5")!.Switch = SwitchPosition.Reverse);
        var system = run.System;
        Assert.AreEqual(SwitchPosition.Reverse, run.InitialSwitches["5"]);

        await LiveSystem.UntilAsync(async () => await system.OnTrackModel(track => track.FindBlock("5")!.Switch) == SwitchPosition.Normal,
            "the Track Controller commands Normal and the Track Model throws switch 5.", TimeSpan.FromSeconds(10));
        await SignalledRouteIntegrationTests.AssertSignalClearsAndTrainMovesAsync(run, signal: "6", otherSignal: "11");
        Assert.AreEqual(SwitchPosition.Normal, system.WaysideBlock("5").ReportedSwitch, "Granted on reported Normal.");

        var seen = await run.FollowUntilAsync("7", TimeSpan.FromSeconds(60), ["1", "2"]);
        CollectionAssert.AreEqual(ToStationB.Take(7).ToArray(), seen.ToArray(), "Through the Normal branch, block by block.");
        Assert.IsFalse(await system.OnTrackModel(track => track.FindBlock("11")!.IsOccupied));
        CollectionAssert.AreEqual(new[] { SwitchPosition.Reverse, SwitchPosition.Normal }, run.Switches.Commanded("5"), "One command, no oscillation.");
        CollectionAssert.AreEqual(new[] { SwitchPosition.Reverse, SwitchPosition.Normal }, run.Switches.Physical("5"), "Thrown once.");
    }

    /// <summary>
    /// Test D: switch 5 cannot move while block 6, in its protection zone (though not on the train's
    /// route to Station C), is occupied, faulted, without valid occupancy feedback, or closed by the
    /// dispatcher. Authority is withheld with the reason, the switch is never thrown, and the train
    /// continues automatically, through the Reverse branch, once the condition genuinely clears.
    /// </summary>
    [TestMethod]
    [Timeout(150_000, CooperativeCancellation = true)]
    [DataRow("occupied", "block 6 is occupied")]
    [DataRow("broken rail", "block 6 reports a fault or closure")]
    [DataRow("track circuit failure", "block 6 is of unknown occupancy")]
    [DataRow("closed by the dispatcher", "block 6 is closed for maintenance")]
    public async Task UnsafeProtectionZone_HoldsTheSwitch_WithholdsAuthority_AndRecoversWhenItClears(string condition, string reason)
    {
        Action<TrackService>? prepare = condition switch
        {
            "occupied" => track => track.ApplyTrainUpdate(new TrackModelTrainUpdateMessage { TrainId = "P6", CurrentBlockId = "6" }),
            "broken rail" => track => track.FindBlock("6")!.BrokenRail = true,
            "track circuit failure" => track => track.FindBlock("6")!.TrackCircuitFailure = true,
            _ => null,
        };
        Func<LiveSystem, Task>? beforeResume = condition == "closed by the dispatcher"
            ? async system =>
            {
                await system.OnCtcAsync(ctc => ctc.CloseBlockAsync("6"));
                await LiveSystem.UntilAsync(() => Task.FromResult(system.WaysideBlock("6").Maintenance == "Closed"), "the Track Controller closes block 6.");
            }
            : null;
        await using var run = await DispatchedRun.StartAsync("Blue", ToStationC, prepare, beforeResume: beforeResume);
        var system = run.System;
        Assert.AreEqual(SwitchPosition.Normal, run.InitialSwitches["5"]);

        // Withheld, with the switch and its reason named at the Track Controller and in CTC.
        string expected = "switch 5 must be Reverse for this route, but it cannot move now: " + reason;
        await LiveSystem.UntilAsync(() => Task.FromResult(system.WaysideBlock("1").MovementReason.Contains(expected, StringComparison.Ordinal)),
            $"the Track Controller withholds the movement: {expected}.", TimeSpan.FromSeconds(15));
        await LiveSystem.UntilAsync(async () => (await run.Train())!.MovementRequest?.AuthorizationReason.Contains(expected, StringComparison.Ordinal) == true,
            "CTC reports why.", TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromSeconds(3)); // several evaluations and Track Model command batches
        Assert.AreEqual(SwitchPosition.Normal, system.WaysideBlock("5").CommandedSwitch, "No throw is commanded.");
        Assert.AreEqual(SwitchPosition.Normal, await system.OnTrackModel(track => track.FindBlock("5")!.Switch), "The switch never moved.");
        Assert.IsFalse((await run.Train())!.MovementRequest!.IsMovementAuthorized);
        Assert.AreEqual(0d, system.WaysideBlock("1").AuthorizedAuthorityMeters);
        Assert.AreEqual(SignalState.Red, system.WaysideBlock("11").CommandedSignal);
        Assert.AreEqual(0d, (await run.TrainModel.StateAsync("001"))?.CommandedAuthorityMeters ?? 0, "Zero authority reaches the train.");
        Assert.AreEqual("1", (await run.Train())!.CurrentBlockId, "The train waits.");

        // The condition clears; the route is set and granted without anyone intervening on the switch.
        switch (condition)
        {
            case "occupied": await system.SendToTrackModelAsync(new TrackModelTrainUpdateMessage { TrainId = "P6", CurrentBlockId = "" }); break;
            case "broken rail":
            case "track circuit failure": await system.SendToTrackModelAsync(new TrackModelFailureCommandMessage { BlockId = "6" }); break;
            default: await system.OnCtcAsync(ctc => ctc.ReopenBlockAsync("6")); break;
        }

        await SignalledRouteIntegrationTests.AssertSignalClearsAndTrainMovesAsync(run, signal: "11", otherSignal: "6");
        Assert.AreEqual(SwitchPosition.Reverse, system.WaysideBlock("5").ReportedSwitch);
        var seen = await run.FollowUntilAsync("11", TimeSpan.FromSeconds(60), ["1", "2"]);
        CollectionAssert.AreEqual(new[] { "1", "2", "3", "4", "5", "11" }, seen.ToArray(), "Onto the Reverse branch.");
        CollectionAssert.AreEqual(new[] { SwitchPosition.Normal, SwitchPosition.Reverse }, run.Switches.Commanded("5"), "Held, then thrown once the zone was safe.");
        CollectionAssert.AreEqual(new[] { SwitchPosition.Normal, SwitchPosition.Reverse }, run.Switches.Physical("5"));
    }

    /// <summary>
    /// Test E: Train 001 is granted toward Station B (Normal) and is moving; while it is still approaching
    /// the junction the dispatcher re-routes it to Station C. The old movement is cancelled, switch 5 stays
    /// approach-locked while the train may still be running on the withdrawn authority, is then thrown
    /// Reverse and confirmed, and the train continues through block 11. Nothing grants over the Normal
    /// branch after the re-route, not even a delayed copy of the old request.
    /// </summary>
    [TestMethod]
    [Timeout(150_000, CooperativeCancellation = true)]
    public async Task RerouteWhileApproaching_RealignsTheSwitch_AndTheOldRouteCanNeverGrantAgain()
    {
        await using var run = await DispatchedRun.StartAsync("Blue", ToStationB);
        var system = run.System;
        await SignalledRouteIntegrationTests.AssertSignalClearsAndTrainMovesAsync(run, signal: "6", otherSignal: "11");
        int sentBefore = system.CtcSent!.Delivered.Count;

        // Re-route from where the train is now (block 2), well before the switch.
        await system.OnCtcAsync(ctc => ctc.UpdateDispatchedTrainPlanAsync("001", "15", DispatchedRun.Departure + TimeSpan.FromSeconds(240)));
        await LiveSystem.UntilAsync(async () => (await run.Train())!.MovementRequest?.Plan.PathBlockIds.Contains("11") == true, "CTC requests the new route.", TimeSpan.FromSeconds(10));
        // The request the re-route cancelled: the train's granted movement over the Normal branch.
        var sent = system.CtcSent.Delivered.OfType<MovementSuggestionMessage>().ToList();
        var cancelled = system.CtcSent.Delivered.Skip(sentBefore).OfType<MovementSuggestionMessage>().First(message => !message.IsActive && message.PathBlockIds.Contains("6"));
        var oldRequest = sent.Last(message => message.IsActive && message.RequestId == cancelled.RequestId);
        Assert.IsFalse(await system.OnTrackModel(track => track.FindBlock("4")!.IsOccupied || track.FindBlock("5")!.IsOccupied), "Re-routed while still approaching the junction.");

        // From now on, nothing may ever grant a movement over block 6.
        var grantsOverTheOldBranch = new List<string>();
        var reasons = new List<string>();
        using var stop = new CancellationTokenSource();
        var monitor = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var block in system.Wayside!.LatestSnapshot.Authorizations.Where(block => block.AuthorizedAuthorityMeters > 0 && block.Request!.PathBlockIds.Contains("6")))
                {
                    grantsOverTheOldBranch.Add($"{block.BlockId}: {block.Request!.RequestId}");
                }

                string reason = system.WaysideBlock((await run.Train())!.CurrentBlockId).MovementReason;
                if (reasons.Count == 0 || reasons[^1] != reason) reasons.Add(reason);
                await Task.Delay(25);
            }
        });

        // A delayed copy of the cancelled request is refused and cannot take the block back.
        int receipts = system.WaysideReceipts.Count;
        await system.SendToTrackControllerAsync(oldRequest);
        await LiveSystem.UntilAsync(() => Task.FromResult(system.WaysideReceipts.Skip(receipts).Any(receipt => !receipt.IsAccepted && receipt.Detail.Contains("cancelled", StringComparison.Ordinal))),
            "the Track Controller refuses the cancelled request.", TimeSpan.FromSeconds(5));

        await LiveSystem.UntilAsync(async () => (await run.Train())!.MovementRequest is { IsMovementAuthorized: true } request && request.Plan.PathBlockIds.Contains("11"),
            "the Track Controller sets the new route and grants it.", TimeSpan.FromSeconds(40));
        Assert.AreEqual(SwitchPosition.Reverse, system.WaysideBlock("5").ReportedSwitch, "Granted on reported Reverse.");
        Assert.AreEqual(SignalState.Red, system.WaysideBlock("6").CommandedSignal);
        var seen = await run.FollowUntilAsync("12", TimeSpan.FromSeconds(60));
        await stop.CancelAsync();
        await monitor;
        Console.WriteLine("Track Controller reasons after the re-route: " + string.Join(" -> ", reasons));
        Console.WriteLine("CTC location sequence after the re-route: " + string.Join(" -> ", seen));

        Assert.IsEmpty(grantsOverTheOldBranch, "Granted over the old branch: " + string.Join("; ", grantsOverTheOldBranch));
        Assert.IsTrue(reasons.Any(reason => reason.Contains("approach locked", StringComparison.Ordinal)), "The switch waited out its approach locking.");
        CollectionAssert.IsSubsetOf(new[] { "5", "11", "12" }, seen.ToArray());
        Assert.IsFalse(seen.Contains("6"), "Never onto the Normal branch.");
        CollectionAssert.AreEqual(new[] { SwitchPosition.Normal, SwitchPosition.Reverse }, run.Switches.Commanded("5"), "Re-aligned once, never back.");
        CollectionAssert.AreEqual(new[] { SwitchPosition.Normal, SwitchPosition.Reverse }, run.Switches.Physical("5"));
    }

    /// <summary>
    /// Test F: a Red Line route from the yard entry RED-9 through two switches that need different
    /// alignments: RED-16 entered from its Reverse leg RED-1 (the Track Model starts it Normal), and RED-27
    /// left by its Normal leg RED-28 (the Track Model starts it Reverse). Each switch's position comes from
    /// the route; the line's other switches are left alone; the train passes RED-16 on its Reverse leg.
    /// </summary>
    [TestMethod]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task RedLine_TwoSwitchesOnOneRoute_EachIsAlignedFromTheRoute_AndTheTrainPassesThem()
    {
        string[] route = ["RED-9", "RED-8", "RED-7", "RED-6", "RED-5", "RED-4", "RED-3", "RED-2", "RED-1", "RED-16", "RED-17",
            "RED-18", "RED-19", "RED-20", "RED-21", "RED-22", "RED-23", "RED-24", "RED-25", "RED-26", "RED-27", "RED-28"];
        await using var run = await DispatchedRun.StartAsync("Red", route, track => track.FindBlock("RED-27")!.Switch = SwitchPosition.Reverse, travelSeconds: 300);
        var system = run.System;
        Assert.AreEqual(SwitchPosition.Normal, run.InitialSwitches["RED-16"]);
        Assert.AreEqual(SwitchPosition.Reverse, run.InitialSwitches["RED-27"]);

        var request = (await run.Train())!.MovementRequest!;
        Console.WriteLine($"CTC request: {request.Plan.TargetBlockId} -> {string.Join(", ", request.Plan.PathBlockIds)}");
        CollectionAssert.IsSubsetOf(new[] { "RED-16", "RED-27" }, request.Plan.PathBlockIds.ToArray(), "One request covers both switches.");

        await LiveSystem.UntilAsync(async () => await system.OnTrackModel(track => track.FindBlock("RED-16")!.Switch == SwitchPosition.Reverse && track.FindBlock("RED-27")!.Switch == SwitchPosition.Normal),
            "the Track Controller aligns RED-16 Reverse and RED-27 Normal and the Track Model throws both.", TimeSpan.FromSeconds(15));
        await LiveSystem.UntilAsync(async () => (await run.Train())!.MovementRequest is { IsMovementAuthorized: true },
            "the Track Controller grants the movement on the reported alignment.", TimeSpan.FromSeconds(20));
        Assert.AreEqual(SwitchPosition.Reverse, system.WaysideBlock("RED-16").ReportedSwitch);
        Assert.AreEqual(SwitchPosition.Normal, system.WaysideBlock("RED-27").ReportedSwitch);
        Assert.IsGreaterThan(0.0, (await run.Train())!.MovementRequest!.AuthorizedAuthorityMeters);

        var seen = await run.FollowUntilAsync("RED-17", TimeSpan.FromSeconds(120));
        Console.WriteLine("CTC location sequence: " + string.Join(" -> ", seen));
        CollectionAssert.AreEqual(route.Take(11).ToArray(), seen.ToArray(), "RED-1 -> RED-16 -> RED-17: through RED-16's Reverse leg.");
        Assert.IsFalse(await system.OnTrackModel(track => track.FindBlock("RED-15")!.IsOccupied), "Never onto RED-16's Normal leg.");

        CollectionAssert.AreEqual(new[] { SwitchPosition.Normal, SwitchPosition.Reverse }, run.Switches.Physical("RED-16"));
        CollectionAssert.AreEqual(new[] { SwitchPosition.Reverse, SwitchPosition.Normal }, run.Switches.Physical("RED-27"));
        foreach (string other in run.InitialSwitches.Keys.Except(["RED-16", "RED-27"]))
        {
            CollectionAssert.AreEqual(new[] { run.InitialSwitches[other] }, run.Switches.Commanded(other), $"Switch {other} is not on the route and is left alone.");
            CollectionAssert.AreEqual(new[] { run.InitialSwitches[other] }, run.Switches.Physical(other), $"Switch {other} never moved.");
        }
    }
}
