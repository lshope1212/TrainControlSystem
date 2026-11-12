using CTC.Core.Models;
using CTC.Core.Scheduling;
using CTC.Core.Services;
using TrackModel.Core.Models;
using TrainControl.Contracts.Enums;

namespace TrainControl.Integration.Tests;

/// <summary>
/// Test F, the primary acceptance test: Train 001 scheduled in the Schedule Builder way from the Green Line yard
/// (GREEN-63) to Edgebrook (GREEN-9) and driven there by every production module over isolated pipes: CTC routes
/// and tracks it, the Track Controller (course deployment, bundled PLC) sets the switches and grants each movement
/// segment, the Track Model maps the Train Model's physics to occupancy, and the Train Controller drives. Nothing
/// sets a switch, a route, an occupancy or a position on the train's behalf; the shared clock runs at 10x.
/// </summary>
/// <remarks>
/// Station stopping, dwell and arrival-time accuracy are out of scope: the train passes every station and the
/// test ends when its front enters GREEN-9 with CTC tracking it there.
/// </remarks>
[TestClass]
public sealed class GreenLineFullRouteIntegrationTests
{
    private static readonly string[] Route = CourseRoutes.YardToEdgebrook;

    /// <summary>The dispatcher's Schedule Builder entries: Green, Train 001, GREEN-63 at 09:00:30, Edgebrook at 10:00:30.</summary>
    private static IReadOnlyList<ScheduledTrain> ScheduleBuilder(CTCService ctc)
    {
        var template = ScheduleTemplateFactory.Create(ctc.State.FindLine("Green")!, TrainIds.Allocate(ctc.State, 1));
        if (template.GetStartBlockId(0) != "GREEN-63" || template.TrainIds[0] != "001")
        {
            throw new InvalidOperationException($"Expected Train 001 from GREEN-63, got {template.TrainIds[0]} from {template.GetStartBlockId(0)}.");
        }

        template.Rows.Single(row => row.BlockId == "GREEN-63").SetTrainTime(0, "09:00:30");
        template.Rows.Single(row => row.BlockId == "GREEN-9").SetTrainTime(0, "10:00:30");
        var result = ScheduleTemplateConverter.Convert(template);
        return result.IsSuccess ? result.Trains : throw new InvalidOperationException(result.ErrorMessage);
    }

    [TestMethod]
    [Timeout(1_200_000, CooperativeCancellation = true)]
    public async Task Train001_YardToEdgebrook_DrivesTheWholeLoopRouteOnRealPhysics()
    {
        var started = DateTime.UtcNow;
        await using var run = await DispatchedRun.StartAsync("Green", ["GREEN-63"], schedule: ScheduleBuilder, clockMultiplier: 10);
        var system = run.System;

        // CTC built the route itself and the Track Model registered exactly that route for the placed train.
        var train = (await run.Train())!;
        CollectionAssert.AreEqual(Route, train.ActiveRouteBlockIds.ToArray(), "CTC filled in all 117 block visits.");
        Assert.AreEqual(36, train.AuthorityLimitRouteIndex, "The first authority segment ends at GREEN-99.");
        var registered = (await system.OnTrackModel(track => track.FindTrackedTrain("001")))!;
        CollectionAssert.AreEqual(Route, registered.PlannedRoute.ToArray(), "The physical dispatch carried the full route.");

        await LiveSystem.UntilAsync(() => Task.FromResult(run.TrainController.Sink.Sent.Any(command => command.TrainId == "001" && command.PowerCommandWatts > 0)),
            "the Train Controller commands traction.", TimeSpan.FromSeconds(30));

        // Follow the train to Edgebrook, sampling what each module says.
        var ctcIndices = new List<int> { train.ActiveRouteIndex };
        var ctcBlocks = new List<string> { train.CurrentBlockId };
        var segmentEnds = new List<int> { train.AuthorityLimitRouteIndex };
        var waits = new List<string>();
        var stops = new List<string>();
        bool stopped = false;
        double maxPosition = 0;
        var nextReport = DateTime.UtcNow;
        await LiveSystem.UntilAsync(async () =>
            {
                var ctc = await system.OnCtc(service => service.State.FindDispatchedTrain("001")!);
                int index = ctc.ActiveRouteIndex;
                if (ctcIndices[^1] != index)
                {
                    ctcIndices.Add(index);
                    ctcBlocks.Add(ctc.CurrentBlockId);
                }

                if (segmentEnds[^1] != ctc.AuthorityLimitRouteIndex) segmentEnds.Add(ctc.AuthorityLimitRouteIndex);
                if (ctc.NextSegmentWaitReason.Length > 0 && (waits.Count == 0 || waits[^1] != ctc.NextSegmentWaitReason)) waits.Add(ctc.NextSegmentWaitReason);

                var state = await run.TrainModel.StateAsync("001");
                var tracked = await system.OnTrackModel(track => track.FindTrackedTrain("001"));
                if (tracked?.Status == TrainTrackingStatus.Faulted)
                {
                    Assert.Fail($"The Track Model faulted the train: {tracked.Fault}");
                }

                if (state is not null)
                {
                    maxPosition = Math.Max(maxPosition, state.PositionMeters);
                    bool isStopped = Math.Abs(state.VelocityMetersPerSecond) < 0.05 && state.PositionMeters > 1;
                    if (isStopped && !stopped) stops.Add($"{tracked?.HeadBlockId} at {state.PositionMeters:0} m (authority {state.CommandedAuthorityMeters:0} m)");
                    stopped = isStopped;
                }

                if (DateTime.UtcNow >= nextReport)
                {
                    nextReport = DateTime.UtcNow.AddSeconds(15);
                    var request = ctc.MovementRequest;
                    Console.WriteLine($"[{run.Clock.Now}] TM pos {state?.PositionMeters:0} v {state?.VelocityMetersPerSecond:0.0} auth {state?.CommandedAuthorityMeters:0} | "
                        + $"Track [{string.Join("+", tracked?.OccupiedBlockIds ?? [])}] | CTC #{index} {ctc.CurrentBlockId} segment end #{ctc.AuthorityLimitRouteIndex} "
                        + $"req {request?.Plan.TargetBlockId}..{request?.Plan.PathBlockIds.LastOrDefault()} {request?.Authorization} {request?.AuthorizedAuthorityMeters:0} m {ctc.NextSegmentWaitReason}");
                }

                return index == Route.Length - 1 && tracked?.HeadBlockId == "GREEN-9";
            },
            "Train 001 drives from GREEN-63 to Edgebrook (GREEN-9) and CTC tracks it there.", TimeSpan.FromMinutes(17));

        var final = (await run.Train())!;
        var path = (await system.OnTrackModel(track => track.FindTrackedTrain("001")))!;
        var model = (await run.TrainModel.StateAsync("001"))!;
        Console.WriteLine($"Arrived after {(DateTime.UtcNow - started).TotalSeconds:0} s wall, simulated {run.Clock.Now}; Train Model at {model.PositionMeters:0} m.");
        Console.WriteLine("CTC segment ends (route index): " + string.Join(" -> ", segmentEnds));
        Console.WriteLine("CTC waits before the next segment: " + string.Join(" | ", waits));
        Console.WriteLine("Train stops on the way: " + (stops.Count == 0 ? "none" : string.Join(" | ", stops)));
        foreach (string id in new[] { "GREEN-77", "GREEN-85", "GREEN-28", "GREEN-13" })
        {
            Console.WriteLine($"Switch {id}: commanded {string.Join(" -> ", run.Switches.Commanded(id))}; Track Model {string.Join(" -> ", run.Switches.Physical(id))}");
        }

        // The physical journey: the front entered exactly the planned blocks, in order, both passes of N included.
        Assert.AreEqual(TrainTrackingStatus.Active, path.Status, path.Fault);
        CollectionAssert.AreEqual(Route, path.TraversedPath.ToArray(), "The Track Model's traversed path is the whole route.");
        Assert.AreEqual("GREEN-9", path.HeadBlockId);
        Assert.IsGreaterThan(14_600.0, path.FrontPositionMeters, "GREEN-9 starts 14 653 m from GREEN-63's entry.");
        Assert.IsLessThan(10.0, Math.Abs(model.PositionMeters - path.FrontPositionMeters), "The Track Model follows the Train Model's physics.");

        // CTC followed every route occurrence, never confusing the two visits of N.
        CollectionAssert.AreEqual(Enumerable.Range(0, Route.Length).ToArray(), ctcIndices.ToArray(), "CTC advanced one route occurrence at a time.");
        CollectionAssert.AreEqual(Route, ctcBlocks.ToArray());
        Assert.AreEqual("GREEN-9", final.CurrentBlockId);
        Assert.AreNotEqual(TrainMovementPhase.EnRoute, final.Phase, "CTC registers the arrival at Edgebrook.");
        CollectionAssert.AreEqual(new[] { 36, 116 }, segmentEnds.ToArray(), "Two authority segments: to GREEN-99, then on to Edgebrook.");

        // Every switch was set by the Track Controller from the route and thrown by the Track Model, once per change.
        CollectionAssert.AreEqual(new[] { SwitchPosition.Normal, SwitchPosition.Reverse }, run.Switches.Commanded("GREEN-77"));
        CollectionAssert.AreEqual(new[] { SwitchPosition.Normal, SwitchPosition.Reverse }, run.Switches.Physical("GREEN-77"));
        CollectionAssert.AreEqual(new[] { SwitchPosition.Normal, SwitchPosition.Reverse }, run.Switches.Commanded("GREEN-85"));
        CollectionAssert.AreEqual(new[] { SwitchPosition.Normal, SwitchPosition.Reverse }, run.Switches.Physical("GREEN-85"));
        CollectionAssert.AreEqual(new[] { SwitchPosition.Normal, SwitchPosition.Reverse }, run.Switches.Physical("GREEN-28"));
        CollectionAssert.AreEqual(new[] { SwitchPosition.Normal }, run.Switches.Physical("GREEN-13"));
        Assert.AreEqual(SwitchPosition.Normal, run.InitialSwitches["GREEN-85"], "Nothing prepositioned 85: it started Normal and was thrown for the second pass.");
    }
}
