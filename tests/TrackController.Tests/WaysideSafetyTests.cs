using TrackController.Contracts.Messages;
using TrackController.Core.Control;
using TrackController.Core.Models;
using TrackController.Core.Safety;
using TrackController.Core.Services;
using TrainControl.Contracts.Enums;

namespace TrackController.Tests;

/// <summary>Exercises WaysideSafety behavior without changing live controller state.</summary>
[TestClass]
public sealed class WaysideSafetyTests
{
    private const string GoodPlc = "allow_route = route_safe and running\nswitch_normal = requested_normal\nsignal_clear = route_safe and running\ncrossing_close = crossing_required";
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        /// <summary>Returns the controlled test time for deterministic watchdog checks.</summary>
        public override DateTimeOffset GetUtcNow() => Now;
        /// <summary>Moves the test clock without delaying the test process.</summary>
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    private sealed class Fixture
    {
        public Clock Clock { get; } = new();
        public TrackControllerService Service
        {
            get;
        }

        /// <summary>Creates a deterministic controller scenario with an injected clock and fresh feedback.</summary>
        public Fixture(bool hardware = false, WaysideConfigurationMessage? configuration = null)
        {
            Service = new(configuration, Clock);
            if (hardware)
                Service.LoadPlc("test.plc", GoodPlc);
            else
                Service.SetHardwareMode(false);
            Feedback();
            Service.SetSimulation(new()
            {
                IsPaused = false
            });
        }

        /// <summary>Supplies fresh sample measurements, with optional changes for the current safety scenario.</summary>
        public void Feedback(Action<List<WaysideBlockFeedback>>? change = null)
        {
            Clock.Advance(0.01);
            var rows = Service.GetConfiguration().Blocks.Select(blockDefinition => new WaysideBlockFeedback { BlockId = blockDefinition.BlockId, ObservedAtUtc = Clock.Now, Occupancy = blockDefinition.BlockId == "5" ? OccupancyState.Occupied : OccupancyState.Clear, Signal = SignalState.Green, Switch = SwitchPosition.Normal, Crossing = CrossingState.Closed, IsReservedByCtc = false }).ToList();
            change?.Invoke(rows);
            Service.ApplyFeedback(new()
            {
                Blocks = rows
            });
        }

        /// <summary>Creates a movement request with an explicit origin and connected sample route.</summary>
        public WaysideMovementRequestMessage Request(string target = "5", params string[] path) => new()
        {
            RequestId = Guid.NewGuid().ToString("N"),
            TrainId = "CTC label",
            TargetBlockId = target,
            FrontOffsetMeters = 0,
            PathBlockIds = path.Length > 0 ? [.. path] : ["6", "7", "8"],
            SuggestedSpeedMetersPerSecond = 12,
            SuggestedAuthorityMeters = 350
        };
        /// <summary>Submits a sample movement request and returns the resulting authorization.</summary>
        public WaysideAuthorization Submit(WaysideMovementRequestMessage? request = null)
        {
            Service.SubmitMovement(request ?? Request());
            return Service.GetSnapshot().Authorizations.Last();
        }

        public WaysideAuthorization Output => Service.GetSnapshot().Authorizations.Last();
    }

    /// <summary>Verifies that startup has no authority red signals and protected crossing.</summary>
    [TestMethod]
    public void GetSnapshot_Startup_ReportsProtectedOutputs()
    {
        var snapshot = new TrackControllerService().GetSnapshot();
        Assert.IsTrue(snapshot.IsPaused);
        Assert.IsEmpty(snapshot.Authorizations);
        Assert.IsTrue(snapshot.Blocks.All(blockStatus => !blockStatus.IsFresh));
        Assert.IsTrue(snapshot.Blocks.Where(blockStatus => blockStatus.HasSignal).All(blockStatus => blockStatus.CommandedSignal == SignalState.Red));
        Assert.AreEqual(CrossingState.Closed, snapshot.Blocks.Single(blockStatus => blockStatus.HasCrossing).CommandedCrossing);
    }

    /// <summary>Verifies that clear route approves metric speed and authority without plc in software mode.</summary>
    [TestMethod]
    public void SubmitMovement_ClearSoftwareRoute_ApprovesMetricLimits()
    {
        var fixture = new Fixture();
        var authorization = fixture.Submit();
        Assert.AreEqual(350d, authorization.AuthorizedAuthorityMeters);
        Assert.AreEqual(12d, authorization.AuthorizedSpeedMetersPerSecond);
    }

    /// <summary>Verifies that hardware cannot resume without aprogram.</summary>
    [TestMethod]
    public void SetSimulation_NoPlcProgram_RejectsResume()
    {
        Assert.Throws<InvalidOperationException>(() => new TrackControllerService().SetSimulation(new() { IsPaused = false }));
    }

    /// <summary>Verifies that loading plc pauses and requires explicit resume.</summary>
    [TestMethod]
    public void LoadPlc_ValidProgram_PausesUntilExplicitResume()
    {
        var fixture = new Fixture(true);
        fixture.Submit();
        fixture.Service.LoadPlc("updated.plc", GoodPlc);
        Assert.IsTrue(fixture.Service.GetSnapshot().IsPaused);
        Assert.AreEqual(0d, fixture.Output.AuthorizedAuthorityMeters);
        fixture.Service.SetSimulation(new()
        {
            IsPaused = false
        });
        Assert.AreEqual(350d, fixture.Output.AuthorizedAuthorityMeters);
    }

    /// <summary>Verifies that failed plc replacement disables previous program.</summary>
    [TestMethod]
    public void LoadPlc_InvalidReplacement_UnloadsPreviousProgram()
    {
        var fixture = new Fixture(true);
        fixture.Submit();
        Assert.Throws<ArgumentException>(() => fixture.Service.LoadPlc("broken", "allow_route = 42"));
        Assert.AreEqual(0d, fixture.Output.AuthorizedAuthorityMeters);
        Assert.Throws<InvalidOperationException>(() => fixture.Service.SetSimulation(new() { IsPaused = false }));
    }

    /// <summary>Verifies that plc can withhold an otherwise safe route.</summary>
    [TestMethod]
    public void SubmitMovement_RestrictivePlc_WithholdsSafeRoute()
    {
        var fixture = new Fixture(true);
        fixture.Service.LoadPlc("stop", GoodPlc.Replace("allow_route = route_safe and running", "allow_route = false"));
        fixture.Service.SetSimulation(new()
        {
            IsPaused = false
        });
        Assert.AreEqual(0d, fixture.Submit().AuthorizedAuthorityMeters);
    }

    /// <summary>Verifies that permissive plc cannot bypass occupied route protection.</summary>
    [TestMethod]
    public void SubmitMovement_PermissivePlc_StillBlocksOccupiedRoute()
    {
        var fixture = new Fixture(true);
        fixture.Service.LoadPlc("permissive", GoodPlc.Replace("route_safe and running", "true"));
        fixture.Service.SetSimulation(new()
        {
            IsPaused = false
        });
        fixture.Feedback(rows => rows.Single(feedback => feedback.BlockId == "7").Occupancy = OccupancyState.Occupied);
        Assert.AreEqual(200d, fixture.Submit().AuthorizedAuthorityMeters);
    }

    /// <summary>Verifies that speed limit uses lowest covered block limit.</summary>
    [TestMethod]
    public void SubmitMovement_MultipleSpeedLimits_UsesLowestCoveredLimit()
    {
        var fixture = new Fixture();
        var request = fixture.Request("10", "11", "12");
        fixture.Feedback(rows => rows.Single(feedback => feedback.BlockId == "10").Occupancy = OccupancyState.Occupied);
        Assert.AreEqual(8d, fixture.Submit(request).AuthorizedSpeedMetersPerSecond);
    }

    /// <summary>Verifies that authority accounts for offset and territory end.</summary>
    [TestMethod]
    public void SubmitMovement_FrontOffsetAndTerritoryEnd_LimitsAuthority()
    {
        var fixture = new Fixture();
        var request = fixture.Request();
        request.FrontOffsetMeters = 60;
        request.SuggestedAuthorityMeters = 900;
        Assert.AreEqual(340d, fixture.Submit(request).AuthorizedAuthorityMeters);
    }

    /// <summary>Verifies that unknown block withholds movement.</summary>
    [TestMethod]
    public void SubmitMovement_UnknownOccupancy_WithholdsMovement()
    {
        var fixture = new Fixture();
        fixture.Feedback(rows => rows.Single(feedback => feedback.BlockId == "7").Occupancy = OccupancyState.Unknown);
        Assert.AreEqual(0d, fixture.Submit().AuthorizedAuthorityMeters);
    }

    /// <summary>Verifies that stale feedback stops and fresh feedback recovers.</summary>
    [TestMethod]
    public void ApplyFeedback_StaleThenFresh_StopsThenRecoversMovement()
    {
        var fixture = new Fixture();
        fixture.Submit();
        fixture.Clock.Advance(5.1);
        Assert.AreEqual(0d, fixture.Output.AuthorizedAuthorityMeters);
        fixture.Feedback();
        Assert.AreEqual(350d, fixture.Output.AuthorizedAuthorityMeters);
    }

    /// <summary>Verifies that fresh feedback cannot revive an expired request.</summary>
    [TestMethod]
    public void ApplyFeedback_ExpiredRequest_DoesNotRestoreMovement()
    {
        var fixture = new Fixture();
        fixture.Submit();
        fixture.Clock.Advance(10.1);
        fixture.Feedback();
        Assert.AreEqual(0d, fixture.Output.AuthorizedAuthorityMeters);
        StringAssert.Contains(fixture.Output.Reason, "expired");
    }

    /// <summary>Verifies that stale samples cannot refresh watchdog.</summary>
    [TestMethod]
    public void ApplyFeedback_OldSamples_DoesNotRefreshWatchdog()
    {
        var fixture = new Fixture();
        fixture.Submit();
        var old = fixture.Clock.Now;
        fixture.Clock.Advance(5.2);
        fixture.Service.ApplyFeedback(new()
        {
            Blocks = [new() { BlockId = "5", ObservedAtUtc = old, Occupancy = OccupancyState.Occupied }]
        });
        Assert.IsFalse(fixture.Service.GetSnapshot().Blocks.Single(blockStatus => blockStatus.BlockId == "5").IsFresh);
    }

    /// <summary>Verifies that switch mismatch waits for real feedback.</summary>
    [TestMethod]
    public void RequestSwitch_MismatchedFeedback_WithholdsMovement()
    {
        var fixture = new Fixture();
        fixture.Feedback(rows => rows.Single(feedback => feedback.BlockId == "6").Switch = SwitchPosition.Reverse);
        Assert.AreEqual(0d, fixture.Submit().AuthorizedAuthorityMeters);
        StringAssert.Contains(fixture.Output.Reason, "needs 7");
        fixture.Feedback();
        Assert.AreEqual(350d, fixture.Output.AuthorizedAuthorityMeters);
    }

    /// <summary>Verifies that switch command does not fabricate feedback.</summary>
    [TestMethod]
    public void RequestSwitch_AcceptedCommand_DoesNotFabricateFeedback()
    {
        var fixture = new Fixture();
        fixture.Feedback(rows => rows.ForEach(feedback => feedback.Occupancy = OccupancyState.Clear));
        fixture.Service.RequestSwitch(new()
        {
            BlockId = "6",
            RequestedPosition = SwitchPosition.Reverse
        });
        var blockStatus = fixture.Service.GetSnapshot().Blocks.Single(blockStatus => blockStatus.BlockId == "6");
        Assert.AreEqual(SwitchPosition.Reverse, blockStatus.CommandedSwitch);
        Assert.AreEqual(SwitchPosition.Normal, blockStatus.ReportedSwitch);
    }

    /// <summary>Verifies that occupied approach blocks switch movement.</summary>
    [TestMethod]
    public void RequestSwitch_OccupiedApproach_RejectsMovement()
    {
        var fixture = new Fixture();
        Assert.Throws<InvalidOperationException>(() => fixture.Service.RequestSwitch(new() { BlockId = "6", RequestedPosition = SwitchPosition.Reverse }));
    }

    /// <summary>Verifies that unknown reservation blocks switch movement.</summary>
    [TestMethod]
    public void RequestSwitch_UnknownReservation_RejectsMovement()
    {
        var fixture = new Fixture();
        fixture.Feedback(rows => rows.ForEach(feedback =>
        {
            feedback.Occupancy = OccupancyState.Clear;
            feedback.IsReservedByCtc = null;
        }));
        Assert.Throws<InvalidOperationException>(() => fixture.Service.RequestSwitch(new() { BlockId = "6", RequestedPosition = SwitchPosition.Reverse }));
    }

    /// <summary>Verifies that maintenance on occupied block allows exit then closes.</summary>
    [TestMethod]
    public void RequestMaintenance_OccupiedBlock_AllowsExitBeforeClosure()
    {
        var fixture = new Fixture();
        fixture.Submit();
        fixture.Service.RequestMaintenance(new()
        {
            BlockId = "5",
            RequestedState = MaintenanceState.Closed
        });
        Assert.AreEqual("Pending closure", fixture.Service.GetSnapshot().Blocks.Single(blockStatus => blockStatus.BlockId == "5").Maintenance);
        Assert.AreEqual(350d, fixture.Output.AuthorizedAuthorityMeters);
        fixture.Feedback(rows => rows.Single(feedback => feedback.BlockId == "5").Occupancy = OccupancyState.Clear);
        Assert.AreEqual("Closed", fixture.Service.GetSnapshot().Blocks.Single(blockStatus => blockStatus.BlockId == "5").Maintenance);
    }

    /// <summary>Verifies that closed block prevents new entry and requires explicit reopen.</summary>
    [TestMethod]
    public void RequestMaintenance_ClosedBlock_RequiresExplicitReopen()
    {
        var fixture = new Fixture();
        fixture.Service.RequestMaintenance(new()
        {
            BlockId = "7",
            RequestedState = MaintenanceState.Closed
        });
        Assert.AreEqual(200d, fixture.Submit().AuthorizedAuthorityMeters);
        fixture.Clock.Advance(5.1);
        fixture.Feedback();
        Assert.AreEqual(200d, fixture.Output.AuthorizedAuthorityMeters);
        fixture.Service.RequestMaintenance(new()
        {
            BlockId = "7",
            RequestedState = MaintenanceState.Open
        });
        Assert.AreEqual(350d, fixture.Output.AuthorizedAuthorityMeters);
    }

    /// <summary>Verifies that faults remain restrictive after connection recovery.</summary>
    [TestMethod]
    public void ApplyFeedback_RecoveredConnectionWithFault_WithholdsMovement()
    {
        var fixture = new Fixture();
        fixture.Submit();
        fixture.Clock.Advance(5.1);
        fixture.Feedback(rows => rows.Single(feedback => feedback.BlockId == "7").IsFaulted = true);
        Assert.AreEqual(0d, fixture.Output.AuthorizedAuthorityMeters);
        StringAssert.Contains(fixture.Output.Reason, "fault");
    }

    /// <summary>Verifies that conflicting requests both withhold authority.</summary>
    [TestMethod]
    public void SubmitMovement_OverlappingRequests_WithholdsBothAuthorities()
    {
        var fixture = new Fixture();
        fixture.Submit();
        fixture.Feedback(rows => rows.Single(feedback => feedback.BlockId == "6").Occupancy = OccupancyState.Occupied);
        fixture.Submit(fixture.Request("6", "7", "8"));
        Assert.IsTrue(fixture.Service.GetSnapshot().Authorizations.All(authorization => authorization.AuthorizedAuthorityMeters == 0));
    }

    /// <summary>Verifies that crossing waits for closed feedback and signals stay red.</summary>
    [TestMethod]
    public void SubmitMovement_CrossingNotConfirmedClosed_HoldsSignalsRed()
    {
        var fixture = new Fixture();
        fixture.Feedback(rows => rows.Single(feedback => feedback.BlockId == "8").Crossing = CrossingState.Open);
        Assert.AreEqual(0d, fixture.Submit().AuthorizedAuthorityMeters);
        Assert.AreEqual(CrossingState.Closed, fixture.Service.GetSnapshot().Blocks.Single(blockStatus => blockStatus.BlockId == "8").CommandedCrossing);
        Assert.IsTrue(fixture.Service.GetSnapshot().Blocks.Where(blockStatus => blockStatus.HasSignal).All(blockStatus => blockStatus.CommandedSignal == SignalState.Red));
        fixture.Feedback();
        Assert.AreEqual(350d, fixture.Output.AuthorizedAuthorityMeters);
    }

    /// <summary>Verifies that crossing remains closed until entire protection zone clears.</summary>
    [TestMethod]
    public void GetSnapshot_OccupiedCrossingZone_KeepsCrossingClosed()
    {
        var fixture = new Fixture();
        fixture.Feedback(rows => rows.Single(feedback => feedback.BlockId == "9").Occupancy = OccupancyState.Occupied);
        Assert.AreEqual(CrossingState.Closed, fixture.Service.GetSnapshot().Blocks.Single(blockStatus => blockStatus.BlockId == "8").CommandedCrossing);
        fixture.Feedback();
        Assert.AreEqual(CrossingState.Open, fixture.Service.GetSnapshot().Blocks.Single(blockStatus => blockStatus.BlockId == "8").CommandedCrossing);
    }

    /// <summary>Verifies that manual controls require maintenance and protection.</summary>
    [TestMethod]
    public void ApplyManual_UnprotectedOrNonMaintenanceMode_RejectsCommand()
    {
        var fixture = new Fixture();
        Assert.Throws<InvalidOperationException>(() => fixture.Service.ApplyManual(new() { BlockId = "4", Signal = SignalState.Red }));
        fixture.Service.SetMaintenanceMode(true);
        fixture.Service.ApplyManual(new()
        {
            BlockId = "4",
            Signal = SignalState.Red
        });
        Assert.Throws<InvalidOperationException>(() => fixture.Service.ApplyManual(new() { BlockId = "6", Switch = SwitchPosition.Reverse }));
        Assert.Throws<InvalidOperationException>(() => fixture.Service.ApplyManual(new() { BlockId = "4", Signal = SignalState.Green }));
    }

    /// <summary>Verifies that paused clock stops but watchdog still runs.</summary>
    [TestMethod]
    public void SetSimulation_Paused_StopsClockButKeepsWatchdogActive()
    {
        var fixture = new Fixture();
        fixture.Clock.Advance(2);
        fixture.Service.SetSimulation(new()
        {
            IsPaused = true,
            SpeedMultiplier = 10
        });
        double elapsed = fixture.Service.GetSnapshot().SimulationSeconds;
        fixture.Clock.Advance(6);
        Assert.AreEqual(elapsed, fixture.Service.GetSnapshot().SimulationSeconds);
        Assert.IsTrue(fixture.Service.GetSnapshot().Blocks.All(blockStatus => !blockStatus.IsFresh));
    }

    /// <summary>Verifies that simulation multiplier applies to clock.</summary>
    [TestMethod]
    public void SetSimulation_SpeedMultiplier_ScalesClock()
    {
        var fixture = new Fixture();
        fixture.Service.SetSimulation(new()
        {
            IsPaused = false,
            SpeedMultiplier = 10
        });
        double before = fixture.Service.GetSnapshot().SimulationSeconds;
        fixture.Clock.Advance(2);
        Assert.AreEqual(20d, fixture.Service.GetSnapshot().SimulationSeconds - before, 0.0001);
    }

    /// <summary>Verifies that cleared origin cannot authorize anew occupant with old request.</summary>
    [TestMethod]
    public void SubmitMovement_RetiredOrigin_RejectsReusedRequest()
    {
        var fixture = new Fixture();
        var request = fixture.Request();
        fixture.Submit(request);
        fixture.Feedback(rows => rows.Single(feedback => feedback.BlockId == "5").Occupancy = OccupancyState.Clear);
        fixture.Feedback();
        Assert.AreEqual(0d, fixture.Output.AuthorizedAuthorityMeters);
        Assert.Throws<ArgumentException>(() => fixture.Service.SubmitMovement(request));
    }

    /// <summary>Verifies that same id cannot change authority origin.</summary>
    [TestMethod]
    public void SubmitMovement_ChangedOriginWithSameId_RejectsRequest()
    {
        var fixture = new Fixture();
        var request = fixture.Request();
        fixture.Submit(request);
        request.FrontOffsetMeters = 5;
        Assert.Throws<ArgumentException>(() => fixture.Service.SubmitMovement(request));
    }

    /// <summary>Verifies that repeated request may change limits but cannot mutate its route.</summary>
    [TestMethod]
    public void SubmitMovement_RepeatedId_AllowsNewLimitsButRejectsNewRoute()
    {
        var fixture = new Fixture();
        var request = fixture.Request();
        fixture.Submit(request);
        request.SuggestedSpeedMetersPerSecond = 3;
        request.SuggestedAuthorityMeters = 100;
        fixture.Submit(request);
        Assert.AreEqual(3d, fixture.Output.AuthorizedSpeedMetersPerSecond);
        Assert.AreEqual(100d, fixture.Output.AuthorizedAuthorityMeters);
        request.PathBlockIds.RemoveAt(request.PathBlockIds.Count - 1);
        Assert.Throws<ArgumentException>(() => fixture.Service.SubmitMovement(request));
        CollectionAssert.AreEqual(new[] { "6", "7", "8" }, fixture.Output.PathBlockIds.ToArray());
    }

    /// <summary>Verifies that configuration copies do not share topology protection or ownership lists.</summary>
    [TestMethod]
    public void GetConfiguration_MutatedCallerLists_PreservesControllerTopology()
    {
        var input = WaysideConfiguration.CreateDemo();
        input.OwnedBlockIds = input.Blocks.Select(block => block.BlockId).ToList();
        var service = new TrackControllerService(input);
        input.Blocks[5].NextBlockIds.Clear();
        input.Blocks[5].SwitchProtectionBlockIds.Clear();
        input.Blocks[7].CrossingProtectionBlockIds.Clear();
        input.OwnedBlockIds.Clear();
        var output = service.GetConfiguration();
        CollectionAssert.AreEqual(new[] { "7", "10" }, output.Blocks[5].NextBlockIds);
        CollectionAssert.AreEqual(new[] { "5", "6", "7", "10" }, output.Blocks[5].SwitchProtectionBlockIds);
        CollectionAssert.AreEqual(new[] { "7", "8", "9" }, output.Blocks[7].CrossingProtectionBlockIds);
        Assert.HasCount(12, output.OwnedBlockIds!);
        output.Blocks[5].NextBlockIds.Clear();
        output.Blocks[5].SwitchProtectionBlockIds.Clear();
        output.Blocks[7].CrossingProtectionBlockIds.Clear();
        output.OwnedBlockIds!.Clear();
        var second = service.GetConfiguration();
        Assert.HasCount(2, second.Blocks[5].NextBlockIds);
        Assert.HasCount(4, second.Blocks[5].SwitchProtectionBlockIds);
        Assert.HasCount(3, second.Blocks[7].CrossingProtectionBlockIds);
        Assert.HasCount(12, second.OwnedBlockIds!);
    }

    /// <summary>Verifies that snapshot lease cannot outlive supporting feedback.</summary>
    [TestMethod]
    public void GetSnapshot_ExpiringFeedback_LimitsOutputLease()
    {
        var fixture = new Fixture();
        fixture.Submit();
        fixture.Clock.Advance(4.9);
        var snapshot = fixture.Service.GetSnapshot();
        Assert.IsLessThanOrEqualTo(0.10001, (snapshot.ValidUntilUtc - snapshot.EvaluatedAtUtc).TotalSeconds);
    }

    /// <summary>Verifies that wrong cancellation id cannot remove new request.</summary>
    [TestMethod]
    public void SubmitMovement_WrongCancellationId_PreservesCurrentRequest()
    {
        var fixture = new Fixture();
        fixture.Submit();
        Assert.Throws<ArgumentException>(() => fixture.Service.SubmitMovement(new() { TargetBlockId = "5", RequestId = "old", IsActive = false }));
        Assert.AreEqual(350d, fixture.Output.AuthorizedAuthorityMeters);
    }

    /// <summary>Verifies that yellow command caps speed before feedback catches up.</summary>
    [TestMethod]
    public void SubmitMovement_YellowSignalCommand_CapsSpeedBeforeFeedback()
    {
        var fixture = new Fixture();
        fixture.Feedback(rows => rows.Single(feedback => feedback.BlockId == "7").Occupancy = OccupancyState.Occupied);
        Assert.AreEqual(5d, fixture.Submit().AuthorizedSpeedMetersPerSecond);
    }

    private static WaysideConfigurationMessage Bidirectional()
    {
        var configuration = WaysideConfiguration.CreateDemo();
        foreach (var edge in configuration.Blocks.SelectMany(blockDefinition => blockDefinition.NextBlockIds.Select(id => (From: blockDefinition.BlockId, To: id))).ToArray())
        {
            var other = configuration.Blocks.Single(blockDefinition => blockDefinition.BlockId == edge.To);
            if (!other.NextBlockIds.Contains(edge.From))
                other.NextBlockIds.Add(edge.From);
        }

        return configuration;
    }

    /// <summary>Verifies that reverse route through aligned switch is supported.</summary>
    [TestMethod]
    public void SubmitMovement_AlignedReverseRoute_ApprovesMovement()
    {
        var fixture = new Fixture(configuration: Bidirectional());
        fixture.Feedback(rows => rows.ForEach(feedback => feedback.Occupancy = feedback.BlockId == "7" ? OccupancyState.Occupied : OccupancyState.Clear));
        Assert.AreEqual(350d, fixture.Submit(fixture.Request("7", "6", "5", "4")).AuthorizedAuthorityMeters);
    }

    /// <summary>Verifies that reverse route cannot enter misaligned switch.</summary>
    [TestMethod]
    public void SubmitMovement_MisalignedReverseRoute_RejectsMovement()
    {
        var fixture = new Fixture(configuration: Bidirectional());
        fixture.Feedback(rows => rows.ForEach(feedback =>
        {
            feedback.Occupancy = feedback.BlockId == "7" ? OccupancyState.Occupied : OccupancyState.Clear;
            feedback.Switch = SwitchPosition.Reverse;
        }));
        Assert.AreEqual(0d, fixture.Submit(fixture.Request("7", "6", "5", "4")).AuthorizedAuthorityMeters);
    }

    /// <summary>Verifies that turnout cannot connect two branches to each other.</summary>
    [TestMethod]
    public void SubmitMovement_BranchToBranchRoute_RejectsMovement()
    {
        var fixture = new Fixture(configuration: Bidirectional());
        fixture.Feedback(rows => rows.ForEach(feedback => feedback.Occupancy = feedback.BlockId == "7" ? OccupancyState.Occupied : OccupancyState.Clear));
        Assert.AreEqual(0d, fixture.Submit(fixture.Request("7", "6", "10")).AuthorizedAuthorityMeters);
    }

    /// <summary>Verifies that origin on switch requires its entry block.</summary>
    [TestMethod]
    public void SubmitMovement_OriginOnSwitch_RequiresEntryBlock()
    {
        var fixture = new Fixture();
        fixture.Feedback(rows => rows.ForEach(feedback => feedback.Occupancy = feedback.BlockId == "6" ? OccupancyState.Occupied : OccupancyState.Clear));
        Assert.AreEqual(0d, fixture.Submit(fixture.Request("6", "7", "8")).AuthorizedAuthorityMeters);
        var request = fixture.Request("6", "7", "8");
        request.EntryBlockId = "5";
        Assert.AreEqual(300d, fixture.Submit(request).AuthorizedAuthorityMeters);
    }

    /// <summary>Verifies that incoming contracts are defensively copied.</summary>
    [TestMethod]
    public void ApplyFeedback_CallerMutatesInput_PreservesAcceptedState()
    {
        var fixture = new Fixture();
        var request = fixture.Request();
        fixture.Submit(request);
        request.PathBlockIds.Clear();
        request.SuggestedSpeedMetersPerSecond = 999;
        Assert.HasCount(3, fixture.Output.PathBlockIds);
        Assert.AreEqual(12d, fixture.Output.AuthorizedSpeedMetersPerSecond);
    }

    /// <summary>Verifies that invalid route and numeric inputs are rejected.</summary>
    [TestMethod]
    public void SubmitMovement_InvalidRouteOrNumbers_RejectsRequest()
    {
        var fixture = new Fixture();
        Assert.Throws<ArgumentException>(() => fixture.Submit(fixture.Request("5", "9")));
        foreach (double value in new[]
        {
            double.NaN,
            double.PositiveInfinity,
            -1d
        }

        )
        {
            var request = fixture.Request();
            request.SuggestedAuthorityMeters = value;
            Assert.Throws<ArgumentException>(() => fixture.Submit(request));
        }

        var noOrigin = fixture.Request();
        noOrigin.FrontOffsetMeters = null;
        Assert.Throws<ArgumentException>(() => fixture.Submit(noOrigin));
    }

    /// <summary>Verifies that invalid feedback batch does not partially apply.</summary>
    [TestMethod]
    public void ApplyFeedback_InvalidBatch_DoesNotPartiallyApply()
    {
        var fixture = new Fixture();
        Assert.Throws<ArgumentException>(() => fixture.Service.ApplyFeedback(new() { Blocks = [new() { BlockId = "5", ObservedAtUtc = fixture.Clock.Now.AddMilliseconds(1), Occupancy = OccupancyState.Clear }, new() { BlockId = "foreign", ObservedAtUtc = fixture.Clock.Now }] }));
        Assert.AreEqual(OccupancyState.Occupied, fixture.Service.GetSnapshot().Blocks.Single(blockStatus => blockStatus.BlockId == "5").Occupancy);
    }

    /// <summary>Verifies that configuration rejects incomplete switch and missing speed limit.</summary>
    [TestMethod]
    public void Configure_IncompleteSwitchOrSpeedLimit_RejectsLayout()
    {
        var configuration = WaysideConfiguration.CreateDemo();
        configuration.Blocks[5].ReverseNextBlockId = null;
        Assert.Throws<ArgumentException>(() => new TrackControllerService(configuration));
        configuration = WaysideConfiguration.CreateDemo();
        configuration.Blocks[0].SpeedLimitMetersPerSecond = 0;
        Assert.Throws<ArgumentException>(() => new TrackControllerService(configuration));
    }

    /// <summary>Verifies that independent guard rejects excessive or misdirected output.</summary>
    [TestMethod]
    public void IsSafe_ExcessiveOrMisdirectedAuthority_RejectsOutput()
    {
        var fixture = new Fixture();
        var authorization = fixture.Submit();
        var snapshot = fixture.Service.GetSnapshot();
        Assert.IsFalse(AuthorizationGuard.IsSafe(authorization with
        {
            AuthorizedAuthorityMeters = 5000
        }, snapshot.Blocks, fixture.Service.GetConfiguration()));
        Assert.IsFalse(AuthorizationGuard.IsSafe(authorization with
        {
            PathBlockIds = new[] { "6", "10", "11" }
        }, snapshot.Blocks, fixture.Service.GetConfiguration()));
    }

    /// <summary>Verifies that boolean parser honors precedence and rejects host code.</summary>
    [TestMethod]
    public void Compile_BooleanExpressions_RespectsPrecedenceAndRejectsHostCode()
    {
        var program = BooleanPlcProgram.Compile(GoodPlc.Replace("allow_route = route_safe and running", "allow_route = false or route_safe and not running"));
        var result = program.Execute(new Dictionary<string, bool> { ["route_safe"] = true, ["switch_safe"] = true, ["requested_normal"] = true, ["crossing_required"] = false, ["running"] = false });
        Assert.IsTrue(result["allow_route"]);
        foreach (string bad in new[]
        {
            "allow_route = System.IO.File.Delete(\"file\")",
            "allow_route = 1",
            "allow_route = missing",
            GoodPlc + "\nallow_route = true",
            GoodPlc.Replace("route_safe and running", new string ('!', 40) + "true")
        }

        )
            Assert.Throws<ArgumentException>(() => BooleanPlcProgram.Compile(bad));
    }

    /// <summary>Verifies that random occupancy never grants entry into an occupied block.</summary>
    [TestMethod]
    public void SubmitMovement_RandomOccupancy_NeverEntersOccupiedBlock()
    {
        var random = new Random(1140);
        for (int sample = 0; sample < 100; sample++)
        {
            var fixture = new Fixture();
            int obstruction = random.Next(6, 9);
            fixture.Feedback(rows => rows.Single(feedback => feedback.BlockId == obstruction.ToString()).Occupancy = OccupancyState.Occupied);
            var authorization = fixture.Submit();
            Assert.IsLessThanOrEqualTo((obstruction - 5) * 100d, authorization.AuthorizedAuthorityMeters);
        }
    }
}
