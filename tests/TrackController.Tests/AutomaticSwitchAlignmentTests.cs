using TrackController.Contracts.Messages;
using TrackController.Core.Services;
using TrackController.Runtime;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Tests;

/// <summary>
/// Automatic switch alignment from accepted CTC routes, with the CTC reservations, approach locking and
/// confirmation rules that protect it. Every case runs with the bundled PLC and in software comparison mode.
/// </summary>
[TestClass]
public sealed class AutomaticSwitchAlignmentTests
{
    private static readonly string[] ToStationB = ["2", "3", "4", "5", "6", "7", "8", "9", "10"];
    private static readonly string[] ToStationC = ["2", "3", "4", "5", "11", "12", "13", "14", "15"];

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    /// <summary>A controller on a course line and a Track Model stand-in that applies its commands as the real one does (an occupied switch is never thrown).</summary>
    private sealed class Line
    {
        private readonly Dictionary<string, TrackModelBlockStateMessage> _track;
        private readonly Dictionary<string, MovementSuggestionMessage> _submitted = [], _renewed = [];

        public Clock Clock { get; } = new();

        public TrackControllerService Service { get; }

        /// <summary>False: the points do not respond to commands (a failed switch machine).</summary>
        public bool SwitchesRespond { get; set; } = true;

        public Line(bool hardware, string territory = "wayside-blue", params string[] occupied)
        {
            var configuration = WaysideDeployment.Read(Path.Combine(AppContext.BaseDirectory, "PiSamples", "course-territories-all-pc.deployment.json"))
                .Territories.First(candidate => candidate.Id.StartsWith(territory + "-", StringComparison.Ordinal)).Configuration;
            // The unit fixture simulates the whole line with one controller.
            configuration.OwnedBlockIds = null;
            Service = new(configuration, Clock);
            if (hardware)
            {
                Service.LoadPlc("wayside.plc", PiRuntimeTests.ProgramSource);
            }
            else
            {
                Service.SetHardwareMode(false);
            }

            _track = configuration.Blocks.ToDictionary(block => block.BlockId, block => new TrackModelBlockStateMessage
            {
                BlockId = block.BlockId,
                Occupancy = occupied.Contains(block.BlockId) ? OccupancyState.Occupied : OccupancyState.Clear,
                Signal = block.HasSignal ? SignalState.Red : SignalState.Unknown,
                Switch = block.NormalNextBlockId is not null ? SwitchPosition.Normal : SwitchPosition.Unknown,
                Crossing = block.HasCrossing ? CrossingState.Open : CrossingState.Unknown,
            });
            Report(applyCommands: false);
            Service.SetSimulation(false, 1);
        }

        public TrackModelBlockStateMessage this[string id] => _track[id];

        /// <summary>One Track Model cycle: apply the controller's latest commands, then report every block.</summary>
        public void Report(Action<Dictionary<string, TrackModelBlockStateMessage>>? change = null, bool applyCommands = true)
        {
            if (applyCommands)
            {
                foreach (var block in Service.GetSnapshot().Blocks)
                {
                    var track = _track[block.BlockId];
                    if (block.HasSignal && block.CommandedSignal is not SignalState.Unknown)
                    {
                        track.Signal = block.CommandedSignal;
                    }

                    if (block.HasSwitch && SwitchesRespond && track.Occupancy == OccupancyState.Clear && block.CommandedSwitch is SwitchPosition.Normal or SwitchPosition.Reverse)
                    {
                        track.Switch = block.CommandedSwitch;
                    }

                    if (block.HasCrossing && block.CommandedCrossing is CrossingState.Open or CrossingState.Closed)
                    {
                        track.Crossing = block.CommandedCrossing;
                    }
                }
            }

            change?.Invoke(_track);
            Clock.Advance(0.1);
            Service.ApplyFeedback(_track.Values.Select(row => row with { ObservedAtUtc = Clock.Now }).ToList());
        }

        /// <summary>Submits (and keeps renewing, see <see cref="Wait"/>) a request for the whole route, as CTC does.</summary>
        public string Submit(string target, string[] path, string trainId = "001", string? entry = null)
        {
            var configuration = Service.GetConfiguration();
            var message = new MovementSuggestionMessage
            {
                RequestId = Guid.NewGuid().ToString("N"),
                TrainId = trainId,
                TargetBlockId = target,
                EntryBlockId = entry,
                PathBlockIds = [.. path],
                SuggestedSpeedMetersPerSecond = 10,
                SuggestedAuthorityMeters = new[] { target }.Concat(path).Sum(id => configuration.Blocks.Single(block => block.BlockId == id).LengthMeters),
            };
            Assert.AreEqual(MovementSubmission.Accepted, Service.SubmitMovement(message));
            _submitted[message.RequestId] = _renewed[message.RequestId] = message;
            return message.RequestId;
        }

        public void Cancel(string requestId)
        {
            var message = _submitted[requestId];
            _renewed.Remove(requestId);
            Service.SubmitMovement(new MovementSuggestionMessage { RequestId = requestId, TrainId = message.TrainId, TargetBlockId = message.TargetBlockId, EntryBlockId = message.EntryBlockId, PathBlockIds = [.. message.PathBlockIds], IsActive = false });
        }

        /// <summary>CTC stops renewing the request (e.g. CTC stopped): its watchdog will expire it.</summary>
        public void StopRenewing(string requestId) => _renewed.Remove(requestId);

        /// <summary>Lets <paramref name="seconds"/> pass in Track Model cycles, renewing the requests CTC still holds every 3 s.</summary>
        public void Wait(double seconds, Action<Dictionary<string, TrackModelBlockStateMessage>>? change = null)
        {
            double renewedAgo = 0;
            for (double elapsed = 0; elapsed < seconds; elapsed += 0.5, renewedAgo += 0.5)
            {
                if (renewedAgo >= 3)
                {
                    foreach (var message in _renewed.Values)
                    {
                        Service.SubmitMovement(message);
                    }

                    renewedAgo = 0;
                }

                Clock.Advance(0.4);
                Report(change);
            }
        }

        public WaysideBlockStatus Block(string id) => Service.GetSnapshot().Blocks.Single(block => block.BlockId == id);
    }

    // ------------------------------------------------------------ Route-derived positions

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ReverseRoute_SetsReverse_ConfirmsIt_ThenGrants_AndHoldsItAfterTheRoute(bool hardware)
    {
        var line = new Line(hardware, occupied: "1");
        string request = line.Submit("1", ToStationC);
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("5").CommandedSwitch, "Commanded as soon as the route is accepted.");
        Assert.AreEqual(0d, line.Block("1").AuthorizedAuthorityMeters, "No authority on a command alone.");

        line.Report(); // the Track Model throws it
        line.Report(); // ...and the next evaluations clear signal 11 and grant on the reported position
        line.Report();
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("5").ReportedSwitch);
        Assert.IsGreaterThan(0d, line.Block("1").AuthorizedAuthorityMeters, line.Block("1").MovementReason);

        // The route ends (cancelled once the train no longer needs it): nothing pulls the switch back.
        line.Cancel(request);
        line.Wait(15);
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("5").CommandedSwitch);
        Assert.AreEqual(SwitchPosition.Reverse, line[ "5"].Switch);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void AlreadyAlignedRoute_CommandsNothingNew(bool hardware)
    {
        var line = new Line(hardware, occupied: "1");
        line.Submit("1", ToStationB);
        for (int i = 0; i < 5; i++)
        {
            Assert.AreEqual(SwitchPosition.Normal, line.Block("5").CommandedSwitch);
            line.Report();
        }

        Assert.IsGreaterThan(0d, line.Block("1").AuthorizedAuthorityMeters, line.Block("1").MovementReason);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RedLine_EachSwitchOnTheRoute_GetsTheLegTheRouteUses(bool hardware)
    {
        // RED-9 toward RED-1 enters RED-16 from its Reverse leg RED-1 and leaves through its stem RED-17,
        // then enters RED-27 from its stem RED-26 and leaves by its Normal leg RED-28.
        string[] route = ["RED-8", "RED-7", "RED-6", "RED-5", "RED-4", "RED-3", "RED-2", "RED-1", "RED-16", "RED-17", "RED-18", "RED-19",
            "RED-20", "RED-21", "RED-22", "RED-23", "RED-24", "RED-25", "RED-26", "RED-27", "RED-28"];
        var line = new Line(hardware, "wayside-red", "RED-9");
        line.Report(rows => rows["RED-27"].Switch = SwitchPosition.Reverse, applyCommands: false);
        line.Submit("RED-9", route);
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("RED-16").CommandedSwitch);
        Assert.AreEqual(SwitchPosition.Normal, line.Block("RED-27").CommandedSwitch);
        foreach (string other in new[] { "RED-33", "RED-38", "RED-44", "RED-52" })
        {
            Assert.AreEqual(SwitchPosition.Normal, line.Block(other).CommandedSwitch, $"{other} is not on the route.");
        }

        Assert.AreEqual(0d, line.Block("RED-9").AuthorizedAuthorityMeters, "Neither switch reports its position yet.");
        line.Report();
        line.Report();
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("RED-16").ReportedSwitch);
        Assert.AreEqual(SwitchPosition.Normal, line.Block("RED-27").ReportedSwitch);
        Assert.IsGreaterThan(0d, line.Block("RED-9").AuthorizedAuthorityMeters, line.Block("RED-9").MovementReason);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RedLine_TrailingThroughASwitch_SetsTheLegTheTrainLeavesBy(bool hardware)
    {
        // RED-75 -> RED-76 -> RED-27 -> RED-26 trails through switch RED-27 from its Reverse leg to its stem.
        var line = new Line(hardware, "wayside-red", "RED-75");
        line.Submit("RED-75", ["RED-76", "RED-27", "RED-26", "RED-25"]);
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("RED-27").CommandedSwitch);
        line.Report();
        line.Report();
        Assert.IsGreaterThan(0d, line.Block("RED-75").AuthorizedAuthorityMeters, line.Block("RED-75").MovementReason);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void GreenLine_RoutesThroughGreen85_GetTheLegTheyUse(bool hardware)
    {
        // GREEN-85: stem GREEN-84, Normal GREEN-86, Reverse GREEN-100. From GREEN-100 the one-way track
        // trails through the Reverse leg toward GREEN-84.
        var line = new Line(hardware, "wayside-green", "GREEN-99");
        line.Submit("GREEN-99", ["GREEN-100", "GREEN-85", "GREEN-84", "GREEN-83"]);
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("GREEN-85").CommandedSwitch);
        Assert.AreEqual(SwitchPosition.Normal, line.Block("GREEN-77").CommandedSwitch, "GREEN-77 is not on the route.");
        line.Wait(1);
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("GREEN-85").ReportedSwitch);
        Assert.IsGreaterThan(0d, line.Block("GREEN-99").AuthorizedAuthorityMeters, line.Block("GREEN-99").MovementReason);
    }

    // ------------------------------------------------------------ Reservations

    // Red Line, switch RED-27 (stem RED-26, Normal RED-28, Reverse RED-76; zone RED-26/27/28/76): train 001
    // eastbound at RED-24 wants the Reverse leg; train 002 westbound at RED-30 holds a route ending in RED-28,
    // in the zone but not through the switch, so the two routes do not overlap.
    private static readonly string[] EastToReverseLeg = ["RED-25", "RED-26", "RED-27", "RED-76", "RED-75"];

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void AnotherRoutesReservation_LocksTheSwitch_UntilItIsReleased(bool hardware)
    {
        var line = new Line(hardware, "wayside-red", "RED-24", "RED-30");
        string other = line.Submit("RED-30", ["RED-29", "RED-28"], trainId: "002");
        line.Submit("RED-24", EastToReverseLeg);
        line.Wait(2);
        Assert.AreEqual(SwitchPosition.Normal, line.Block("RED-27").CommandedSwitch);
        Assert.AreEqual(SwitchPosition.Normal, line["RED-27"].Switch);
        StringAssert.Contains(line.Block("RED-27").SwitchAlignment, "switch RED-27 must be Reverse for this route, but it cannot move now: block RED-28 is reserved by CTC for train 002");
        StringAssert.Contains(line.Block("RED-24").MovementReason, "block RED-28 is reserved by CTC for train 002");
        Assert.IsTrue(line.Block("RED-28").IsReservedByCtc);
        Assert.AreEqual(0d, line.Block("RED-24").AuthorizedAuthorityMeters);
        Assert.IsGreaterThan(0d, line.Block("RED-30").AuthorizedAuthorityMeters, "The other train's route is unaffected.");

        line.Cancel(other);
        line.Wait(TrackControllerService.SwitchApproachLockSeconds + 1); // train 002's authority covered RED-28
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("RED-27").ReportedSwitch, "Released: the route is set.");
        Assert.IsGreaterThan(0d, line.Block("RED-24").AuthorizedAuthorityMeters, line.Block("RED-24").MovementReason);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ExpiredReservation_StillLocksTheSwitch_UntilCtcCancelsIt(bool hardware)
    {
        var line = new Line(hardware, "wayside-red", "RED-24", "RED-30");
        line.Service.SetSimulation(true); // nothing is granted to train 002 before its request lapses
        string other = line.Submit("RED-30", ["RED-29", "RED-28"], trainId: "002");
        line.StopRenewing(other); // CTC's renewals for train 002 stop; its watchdog expires it.
        line.Wait(11);
        line.Service.SetSimulation(false);
        line.Submit("RED-24", EastToReverseLeg);
        line.Wait(2);
        StringAssert.Contains(line.Block("RED-27").SwitchAlignment, "train 002 (request");
        StringAssert.Contains(line.Block("RED-27").SwitchAlignment, "whose request expired without being cancelled");
        Assert.AreEqual(SwitchPosition.Normal, line.Block("RED-27").CommandedSwitch, "Stale reservations restrict.");
        Assert.AreEqual(0d, line.Block("RED-30").AuthorizedAuthorityMeters, "The expired request authorizes nothing.");

        line.Cancel(other);
        line.Report();
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("RED-27").CommandedSwitch);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void TwoRoutesNeedingTheSwitch_HoldIt_WithoutOscillating(bool hardware)
    {
        var line = new Line(hardware, occupied: ["1", "3"]);
        line.Submit("3", ["4", "5", "6"], trainId: "002");
        line.Submit("1", ToStationC);
        for (int i = 0; i < 10; i++)
        {
            Assert.AreEqual(SwitchPosition.Normal, line.Block("5").CommandedSwitch, $"cycle {i}");
            line.Report();
        }

        Assert.AreEqual(0d, line.Block("1").AuthorizedAuthorityMeters);
        Assert.AreEqual(0d, line.Block("3").AuthorizedAuthorityMeters, "Overlapping routes are both withheld.");
        StringAssert.Contains(line.Block("5").SwitchAlignment, "claimed by 2 routes");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void PendingRoute_IsNotLockedByItsOwnReservation(bool hardware)
    {
        var line = new Line(hardware, occupied: "1");
        line.Submit("1", ToStationC);
        Assert.IsTrue(line.Block("5").IsReservedByCtc, "The route reserves the zone...");
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("5").CommandedSwitch, "...and that reservation does not lock its own switch.");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void DispatcherSwitchRequest_IsRefused_WhileARouteReservesTheZone(bool hardware)
    {
        var line = new Line(hardware, occupied: "1");
        line.Submit("1", ToStationC);
        line.Report();
        var exception = Assert.Throws<InvalidOperationException>(() => line.Service.RequestSwitch(new() { BlockId = "5", RequestedPosition = SwitchPosition.Normal }));
        StringAssert.Contains(exception.Message, "reserved by CTC for train 001");
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("5").CommandedSwitch);
    }

    // ------------------------------------------------------------ Unsafe zones

    [TestMethod]
    [DataRow(true, "6 occupied", "block 6 is occupied")]
    [DataRow(false, "6 occupied", "block 6 is occupied")]
    [DataRow(true, "6 broken rail", "block 6 reports a fault or closure")]
    [DataRow(false, "11 power failure", "block 11 reports a fault or closure")]
    [DataRow(true, "4 occupancy unknown", "block 4 is of unknown occupancy")]
    [DataRow(true, "6 feedback missing", "block 6 needs fresh feedback")]
    [DataRow(false, "6 feedback missing", "block 6 needs fresh feedback")]
    [DataRow(true, "11 maintenance", "block 11 is closed for maintenance")]
    [DataRow(false, "6 track closure", "block 6 reports a fault or closure")]
    public void UnsafeProtectionZone_HoldsTheSwitch_AndTheRouteRecoversWhenItClears(bool hardware, string condition, string reason)
    {
        var line = new Line(hardware, occupied: "1");
        Action<Dictionary<string, TrackModelBlockStateMessage>>? unsafeZone = condition switch
        {
            "6 occupied" => rows => rows["6"].Occupancy = OccupancyState.Occupied,
            "6 broken rail" => rows => rows["6"].BrokenRail = true,
            "11 power failure" => rows => rows["11"].PowerFailure = true,
            "4 occupancy unknown" => rows => rows["4"].Occupancy = OccupancyState.Unknown,
            "6 track closure" => rows => rows["6"].IsClosed = true,
            _ => null,
        };
        if (condition == "11 maintenance")
        {
            line.Service.RequestMaintenance(new() { BlockId = "11", RequestedState = MaintenanceState.Closed });
        }

        var stale = condition == "6 feedback missing";
        void Cycle()
        {
            if (stale)
            {
                // Every block but 6 keeps reporting.
                line.Clock.Advance(0.5);
                line.Service.ApplyFeedback(line.Service.GetSnapshot().Blocks.Where(block => block.BlockId != "6")
                    .Select(block => line[block.BlockId] with { ObservedAtUtc = line.Clock.Now }).ToList());
            }
            else
            {
                line.Report(unsafeZone);
            }
        }

        // Long enough for block 6's last report to go stale (the feedback timeout is 5 s).
        for (int i = 0; i < (stale ? 12 : 1); i++)
        {
            Cycle();
        }

        line.Submit("1", ToStationC);
        for (int i = 0; i < 15; i++)
        {
            Cycle();
            Assert.AreEqual(SwitchPosition.Normal, line.Block("5").CommandedSwitch, $"{condition}, cycle {i}");
            Assert.AreEqual(0d, line.Block("1").AuthorizedAuthorityMeters, $"{condition}, cycle {i}");
        }

        Assert.AreEqual(SwitchPosition.Normal, line["5"].Switch, "Never thrown.");
        StringAssert.Contains(line.Block("5").SwitchAlignment, "switch 5 must be Reverse for this route, but it cannot move now: " + reason);
        Assert.AreEqual("Withheld", line.Block("1").MovementStatus);

        // The condition clears; the same request is set and granted with nothing else done.
        if (condition == "11 maintenance")
        {
            line.Service.RequestMaintenance(new() { BlockId = "11", RequestedState = MaintenanceState.Open });
        }

        line.Report(rows =>
        {
            rows["6"].Occupancy = rows["4"].Occupancy = OccupancyState.Clear;
            rows["6"].BrokenRail = rows["11"].PowerFailure = rows["6"].IsClosed = false;
        });
        line.Wait(2);
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("5").ReportedSwitch, condition);
        Assert.IsGreaterThan(0d, line.Block("1").AuthorizedAuthorityMeters, line.Block("1").MovementReason);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Paused_NoAutomaticThrow_UntilResumed(bool hardware)
    {
        var line = new Line(hardware, occupied: "1");
        line.Service.SetSimulation(true);
        line.Submit("1", ToStationC);
        line.Report();
        Assert.AreEqual(SwitchPosition.Normal, line.Block("5").CommandedSwitch);
        StringAssert.Contains(line.Block("5").SwitchAlignment, "the simulation is paused");

        line.Service.SetSimulation(false);
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("5").CommandedSwitch);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void MaintenanceMode_LeavesTheSwitchToTheMaintainer(bool hardware)
    {
        var line = new Line(hardware, occupied: "1");
        line.Service.SetMaintenanceMode(true);
        line.Submit("1", ToStationC);
        line.Report();
        Assert.AreEqual(SwitchPosition.Normal, line.Block("5").CommandedSwitch);
        line.Service.SetMaintenanceMode(false);
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("5").CommandedSwitch);
    }

    // ------------------------------------------------------------ Feedback, confirmation and stability

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void UnconfirmedThrow_IsReported_AndNeverGrantsAuthority(bool hardware)
    {
        var line = new Line(hardware, occupied: "1") { SwitchesRespond = false };
        line.Submit("1", ToStationC);
        line.Wait(1);
        StringAssert.Contains(line.Block("1").MovementReason, "waiting for the Track Model to report Reverse");
        line.Wait(8);
        StringAssert.Contains(line.Block("1").MovementReason, "the Track Model still reports Normal: the throw is not confirmed");
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("5").CommandedSwitch, "Still safe to move: it keeps being commanded.");
        Assert.AreEqual(0d, line.Block("1").AuthorizedAuthorityMeters);
        Assert.AreEqual(SignalState.Red, line.Block("11").CommandedSignal);

        // The points respond after all.
        line.SwitchesRespond = true;
        line.Wait(1);
        Assert.IsGreaterThan(0d, line.Block("1").AuthorizedAuthorityMeters);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ThrowInProgress_IsCompleted_NotReversed_WhenTheRouteIsCancelled(bool hardware)
    {
        var line = new Line(hardware, occupied: "1") { SwitchesRespond = false };
        string request = line.Submit("1", ToStationC);
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("5").CommandedSwitch);
        line.Cancel(request);
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("5").CommandedSwitch, "A moving switch is never commanded back mid-stroke.");
        line.SwitchesRespond = true;
        line.Report();
        line.Report();
        Assert.AreEqual(SwitchPosition.Reverse, line["5"].Switch);
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("5").CommandedSwitch, "And it stays where it went.");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ThrowThatNeverHappened_FallsBackToTheReportedPosition_OnceTheZoneIsUnsafe(bool hardware)
    {
        var line = new Line(hardware, occupied: "1") { SwitchesRespond = false };
        line.Submit("1", ToStationC);
        line.Wait(7); // beyond the confirmation time
        line.Report(rows => rows["6"].Occupancy = OccupancyState.Occupied);
        Assert.AreEqual(SwitchPosition.Normal, line.Block("5").CommandedSwitch, "No stale throw is left commanded into an unsafe zone.");
    }

    // ------------------------------------------------------------ Route changes and approach locking

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Reroute_AfterAGrant_IsApproachLocked_ThenSetsTheNewBranch(bool hardware)
    {
        var line = new Line(hardware, occupied: "1");
        string toB = line.Submit("1", ToStationB);
        line.Wait(1);
        Assert.IsGreaterThan(0d, line.Block("1").AuthorizedAuthorityMeters, "Granted toward Station B.");

        line.Cancel(toB);
        line.Submit("1", ToStationC);
        line.Wait(5);
        Assert.AreEqual(SwitchPosition.Normal, line.Block("5").CommandedSwitch, "The train may still be running on the withdrawn authority.");
        StringAssert.Contains(line.Block("1").MovementReason, "approach locked");
        Assert.AreEqual(0d, line.Block("1").AuthorizedAuthorityMeters);
        Assert.AreEqual(SignalState.Red, line.Block("6").CommandedSignal, "The old branch is never protected again.");

        line.Wait(TrackControllerService.SwitchApproachLockSeconds);
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("5").ReportedSwitch);
        Assert.IsGreaterThan(0d, line.Block("1").AuthorizedAuthorityMeters, line.Block("1").MovementReason);
        Assert.AreEqual(SignalState.Red, line.Block("6").CommandedSignal);
        Assert.AreEqual(SignalState.Green, line.Block("11").CommandedSignal);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Reroute_BeforeAnyGrant_SetsTheNewBranchAtOnce(bool hardware)
    {
        var line = new Line(hardware, occupied: "1") { SwitchesRespond = false };
        string toC = line.Submit("1", ToStationC);
        line.Report();
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("5").CommandedSwitch);
        line.SwitchesRespond = true;
        line.Report();
        Assert.AreEqual(SwitchPosition.Reverse, line["5"].Switch);

        // Re-planned before it was granted: no approach locking is needed, the switch comes back.
        line.Cancel(toC);
        line.Submit("1", ToStationB);
        line.Wait(2);
        Assert.AreEqual(SwitchPosition.Normal, line.Block("5").ReportedSwitch);
        Assert.IsGreaterThan(0d, line.Block("1").AuthorizedAuthorityMeters, line.Block("1").MovementReason);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void CopyOfACancelledRoute_IsRefused_AndCannotTakeTheBlockBack(bool hardware)
    {
        var line = new Line(hardware, occupied: "1");
        var old = new MovementSuggestionMessage { RequestId = "old-route", TrainId = "001", TargetBlockId = "1", PathBlockIds = [.. ToStationB], SuggestedSpeedMetersPerSecond = 10, SuggestedAuthorityMeters = 500 };
        line.Service.SubmitMovement(old);
        line.Service.SubmitMovement(new MovementSuggestionMessage { RequestId = "old-route", TrainId = "001", TargetBlockId = "1", PathBlockIds = [.. ToStationB], IsActive = false });
        line.Submit("1", ToStationC);

        var exception = Assert.Throws<ArgumentException>(() => line.Service.SubmitMovement(old));
        StringAssert.Contains(exception.Message, "cancelled");
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("5").CommandedSwitch, "The replacement route keeps its switch.");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void CtcStopsRenewing_TheGrantLapses_AndTheSwitchStaysLocked(bool hardware)
    {
        var line = new Line(hardware, occupied: ["1", "8"]);
        string granted = line.Submit("1", ["2", "3", "4", "5", "6", "7"]);
        line.Wait(1);
        Assert.IsGreaterThan(0d, line.Block("1").AuthorizedAuthorityMeters);

        line.StopRenewing(granted);
        line.Wait(12);
        Assert.AreEqual(0d, line.Block("1").AuthorizedAuthorityMeters, "The watchdog withdrew the authority.");
        var exception = Assert.Throws<InvalidOperationException>(() => line.Service.RequestSwitch(new() { BlockId = "5", RequestedPosition = SwitchPosition.Reverse }));
        StringAssert.Contains(exception.Message, "expired without being cancelled");
        Assert.AreEqual(SwitchPosition.Normal, line.Block("5").CommandedSwitch);
    }
}
