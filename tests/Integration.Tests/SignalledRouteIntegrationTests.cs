using CTC.Core.Models;
using TrackModel.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Integration.Tests;

/// <summary>
/// The live Blue Line demonstration through the signalled blocks, with every production component over
/// isolated pipes: CTC dispatches Train 001 from block 1; the Track Controller (course deployment, bundled
/// <c>wayside.plc</c>) prepares the route and commands the protecting signal; the real Track Model applies
/// that command and reports the aspect back; only then is authority granted; it reaches the production
/// Train Model and Train Controller, and the train moves on real physics.
/// </summary>
/// <remarks>
/// Regression for "Withheld: Waiting for proceed signal feedback at block 6", which used to last forever.
/// No test sets a signal itself: every aspect seen here was commanded by the Track Controller and
/// applied by the Track Model.
/// </remarks>
[TestClass]
public sealed class SignalledRouteIntegrationTests
{
    internal static readonly string[] ToStationB = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "10"];
    internal static readonly string[] ToStationC = ["1", "2", "3", "4", "5", "11", "12", "13", "14", "15"];

    private static Task<DispatchedRun> StartAsync(string[] route, Action<TrackService>? prepareTrack = null) => DispatchedRun.StartAsync("Blue", route, prepareTrack);

    /// <summary>Waits for the grant, then checks it end to end: real aspect, positive values at the Train Model and Train Controller, and motion.</summary>
    internal static async Task AssertSignalClearsAndTrainMovesAsync(DispatchedRun run, string signal, string otherSignal)
    {
        var system = run.System;
        var reasons = new List<string>();
        await LiveSystem.UntilAsync(async () =>
            {
                var request = (await run.Train())!.MovementRequest;
                string reason = system.WaysideBlock("1").MovementReason;
                if (reasons.Count == 0 || reasons[^1] != reason) reasons.Add(reason);
                return request is { IsMovementAuthorized: true };
            },
            "the Track Controller clears the signal and grants the movement.", TimeSpan.FromSeconds(20));
        Console.WriteLine("Track Controller reasons until the grant: " + string.Join(" -> ", reasons));

        // The aspect that permitted the grant was applied by the Track Model and reported back.
        var protecting = system.WaysideBlock(signal);
        Assert.AreEqual(SignalState.Green, protecting.CommandedSignal);
        Assert.AreEqual(SignalState.Green, protecting.ReportedSignal, "Granted on the Track Model's reported aspect.");
        Assert.AreEqual(SignalState.Green, await system.OnTrackModel(track => track.FindBlock(signal)!.Signal));
        Assert.AreEqual(SignalState.Red, system.WaysideBlock(otherSignal).CommandedSignal, "The branch not taken stays Red.");

        var granted = (await run.Train())!.MovementRequest!;
        Assert.IsGreaterThan(0.0, granted.AuthorizedAuthorityMeters);
        Assert.IsGreaterThan(0.0, granted.AuthorizedSpeedMetersPerSecond);

        // The commanded values reach the Train Model and the Train Controller.
        await LiveSystem.UntilAsync(async () => (await run.TrainModel.StateAsync("001")) is { CommandedAuthorityMeters: > 0, CommandedSpeedMetersPerSecond: > 0 },
            "the Train Model receives positive commanded speed and authority.", TimeSpan.FromSeconds(10));
        await LiveSystem.UntilAsync(() => Task.FromResult(run.TrainController.Subsystem.Registry.Get("TRAIN-001").LastModelInput is { RemainingAuthorityMeters: > 0, CommandedSpeedMetersPerSecond: > 0 }),
            "the Train Controller receives positive commanded speed and authority.", TimeSpan.FromSeconds(10));
        await LiveSystem.UntilAsync(() => Task.FromResult(run.TrainController.Sink.Sent.Any(command => command.TrainId == "001" && command.PowerCommandWatts > 0 && !command.ServiceBrakeCommand && !command.EmergencyBrakeCommand)),
            "the Train Controller releases its brakes and commands traction.", TimeSpan.FromSeconds(10));

        // The train moves on physics; the Track Model, Track Controller and CTC follow it into block 2.
        await LiveSystem.UntilAsync(async () => (await run.Train())!.CurrentBlockId == "2", "CTC tracks Train 001 into block 2.", TimeSpan.FromSeconds(40));
        Assert.AreEqual(OccupancyState.Occupied, system.WaysideBlock("2").Occupancy);
        Assert.IsGreaterThan(0.0, (await run.TrainModel.StateAsync("001"))!.PositionMeters);
    }

    /// <summary>
    /// Test A: the live demonstration route, A1 toward Station B (block 10) through signal 6. Switch 5
    /// already lies Normal, as this route needs: it is neither commanded elsewhere nor moved.
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Train001_ToStationB_ClearsSignal6_IsAuthorized_AndMoves()
    {
        await using var run = await StartAsync(ToStationB);
        await AssertSignalClearsAndTrainMovesAsync(run, signal: "6", otherSignal: "11");

        // On through the switch and past the signal: CTC follows the train block by block into block 7,
        // with authority handed from block to block and no emergency brake on the way.
        var seen = new List<string> { "1", "2" };
        await LiveSystem.UntilAsync(async () =>
            {
                string current = (await run.Train())!.CurrentBlockId;
                if (seen[^1] != current) seen.Add(current);
                return current == "7";
            },
            "CTC tracks Train 001 past signal 6 into block 7.", TimeSpan.FromSeconds(60));
        Console.WriteLine("CTC location sequence: " + string.Join(" -> ", seen));
        CollectionAssert.AreEqual(ToStationB.Take(7).ToArray(), seen.ToArray(), "Block by block, never skipping one.");
        // Already aligned: the route needs nothing thrown, and nothing is.
        Assert.AreEqual(SwitchPosition.Normal, run.InitialSwitches["5"]);
        CollectionAssert.AreEqual(new[] { SwitchPosition.Normal }, run.Switches.Commanded("5"), "No unnecessary switch command.");
        CollectionAssert.AreEqual(new[] { SwitchPosition.Normal }, run.Switches.Physical("5"), "Switch 5 never moved.");
        Assert.IsTrue(run.TrainController.Sink.Sent.Where(command => command.TrainId == "001").All(command => !command.EmergencyBrakeCommand),
            "Through the signalled block without a false zero.");
    }

    /// <summary>
    /// Test B (primary acceptance for automatic switch alignment): the alternate branch, toward Station C
    /// (block 15) through signal 11. The Track Model starts in its normal configuration with switch 5
    /// Normal; nothing in the test touches the switch. The route alone makes the Track Controller command
    /// Reverse, the Track Model throws it and reports it, and only then is authority granted. The train
    /// then takes the Reverse branch into block 11 on real physics.
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Train001_ToStationC_ClearsSignal11_IsAuthorized_AndMoves()
    {
        await using var run = await StartAsync(ToStationC);
        var system = run.System;
        Assert.AreEqual(SwitchPosition.Normal, run.InitialSwitches["5"], "Start-up configuration: switch 5 Normal.");

        // The accepted request, not a switch request, makes the controller establish Reverse.
        await LiveSystem.UntilAsync(async () => (await run.Train())?.MovementRequest?.Acknowledgement is RequestAcknowledgementOutcome.AcceptedForEvaluation or RequestAcknowledgementOutcome.PreparedAsSuccessor,
            "the Track Controller accepts CTC's movement request.", TimeSpan.FromSeconds(10));
        await LiveSystem.UntilAsync(() => Task.FromResult(system.WaysideBlock("5").CommandedSwitch == SwitchPosition.Reverse),
            "the Track Controller commands switch 5 Reverse for the route.", TimeSpan.FromSeconds(10));
        await LiveSystem.UntilAsync(async () => await system.OnTrackModel(track => track.FindBlock("5")!.Switch) == SwitchPosition.Reverse,
            "the Track Model throws switch 5 Reverse.", TimeSpan.FromSeconds(10));
        await LiveSystem.UntilAsync(() => Task.FromResult(system.WaysideBlock("5").ReportedSwitch == SwitchPosition.Reverse),
            "the Track Controller receives Reverse feedback.", TimeSpan.FromSeconds(10));
        Assert.IsFalse(system.CtcSent!.Delivered.OfType<SwitchPositionRequestMessage>().Any(), "CTC never asked for the switch: the route did.");

        await AssertSignalClearsAndTrainMovesAsync(run, signal: "11", otherSignal: "6");
        Assert.AreEqual(SwitchPosition.Reverse, system.WaysideBlock("5").ReportedSwitch);

        // On through the switch: the train takes the Reverse branch, 4 -> 5 -> 11, block by block.
        var seen = new List<string> { "1", "2" };
        await LiveSystem.UntilAsync(async () =>
            {
                string current = (await run.Train())!.CurrentBlockId;
                if (seen[^1] != current) seen.Add(current);
                return current == "12";
            },
            "CTC tracks Train 001 through switch 5 onto the Reverse branch (block 12).", TimeSpan.FromSeconds(60));
        Console.WriteLine("CTC location sequence: " + string.Join(" -> ", seen));
        CollectionAssert.AreEqual(new[] { "1", "2", "3", "4", "5", "11", "12" }, seen.ToArray(), "Block by block onto the Reverse branch.");
        Assert.IsFalse(await system.OnTrackModel(track => track.FindBlock("6")!.IsOccupied), "Never onto the Normal branch.");
        Assert.AreEqual(SwitchPosition.Reverse, await system.OnTrackModel(track => track.FindBlock("5")!.Switch), "Not thrown back behind the train.");
        CollectionAssert.AreEqual(new[] { SwitchPosition.Normal, SwitchPosition.Reverse }, run.Switches.Commanded("5"), "One command, no oscillation.");
        CollectionAssert.AreEqual(new[] { SwitchPosition.Normal, SwitchPosition.Reverse }, run.Switches.Physical("5"), "Thrown once.");
        Assert.IsTrue(run.TrainController.Sink.Sent.Where(command => command.TrainId == "001").All(command => !command.EmergencyBrakeCommand),
            "Through the junction without a false zero.");
    }

    /// <summary>
    /// Test D: after the grant through signal 6, a broken rail in block 7 (inside the protected route)
    /// withdraws authority at once, signal 6 is commanded Red and the Track Model shows it, the Train Model
    /// receives zero authority, and the Train Controller brakes on its own protections.
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AfterTheGrant_BrokenRailBeyondSignal6_RevokesAuthority_RestrictsTheSignal_AndTheTrainBrakes()
    {
        await using var run = await StartAsync(ToStationB);
        var system = run.System;
        await LiveSystem.UntilAsync(() => Task.FromResult(run.TrainController.Sink.Sent.Any(command => command.TrainId == "001" && command.PowerCommandWatts > 0)),
            "the train is granted through signal 6 and takes traction.", TimeSpan.FromSeconds(25));
        Assert.AreEqual(SignalState.Green, await system.OnTrackModel(track => track.FindBlock("6")!.Signal));

        int sentBefore = run.TrainController.Sink.Sent.Count;
        await system.SendToTrackModelAsync(new TrackModelFailureCommandMessage { BlockId = "7", BrokenRail = true });

        await LiveSystem.UntilAsync(() => Task.FromResult(system.Wayside!.LatestSnapshot.Authorizations.All(block => block.AuthorizedAuthorityMeters == 0)),
            "the Track Controller withdraws the authority.", TimeSpan.FromSeconds(5));
        Assert.AreEqual(SignalState.Red, system.WaysideBlock("6").CommandedSignal, "Signal 6 is re-evaluated and commanded Red.");
        await LiveSystem.UntilAsync(async () => await system.OnTrackModel(track => track.FindBlock("6")!.Signal) == SignalState.Red,
            "the Track Model applies the restrictive aspect.", TimeSpan.FromSeconds(5));
        await LiveSystem.UntilAsync(async () => (await run.TrainModel.StateAsync("001")) is { CommandedAuthorityMeters: 0 },
            "the Train Model receives zero authority.", TimeSpan.FromSeconds(5));
        await LiveSystem.UntilAsync(() => Task.FromResult(run.TrainController.Sink.Sent.Skip(sentBefore).Any(command => command.TrainId == "001" && command.PowerCommandWatts == 0 && (command.ServiceBrakeCommand || command.EmergencyBrakeCommand))),
            "the Train Controller cuts traction and brakes.", TimeSpan.FromSeconds(10));
        Assert.IsFalse((await run.Train())!.MovementRequest!.IsMovementAuthorized, "CTC reports the withdrawal.");
    }
}
