using TrackController.Contracts.Messages;
using TrackController.Core.Models;
using TrackController.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Tests;

/// <summary>
/// Movement authority continuity at block boundaries (prepare, transfer, retire) and the safety
/// properties that must survive it: no false zero for a valid handoff, no added authority, no
/// overlapping grants, and an immediate, unsuppressed zero for every genuine revocation.
/// </summary>
/// <remarks>
/// The demonstration territory: blocks 1-12, 100 m each, 15 m/s (10-12: 8 m/s); signals at 4, 6
/// and 8; switch 6 (stem 5, Normal 7, Reverse 10); crossing 8. "Train authority" below is what the
/// wayside codes for the train in a block, exactly as the Track Model output carries it: the grant of
/// the request targeting the block, or the prepared continuation for a train that entered it from the
/// continuation's entry block, otherwise zero.
/// </remarks>
[TestClass]
public sealed class AuthorityHandoffTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    private sealed class Territory
    {
        public Clock Clock { get; } = new();

        public TrackControllerService Service { get; }

        /// <summary>Occupied blocks, and where each occupying train came from (empty when placed).</summary>
        public Dictionary<string, string> Trains { get; } = new() { ["1"] = "" };

        public Territory(bool hardware, WaysideConfigurationMessage? configuration = null)
        {
            Service = new TrackControllerService(configuration, Clock);
            if (hardware)
            {
                Service.LoadPlc("test.plc", PiRuntimeTests.ProgramSource);
            }
            else
            {
                Service.SetHardwareMode(false);
            }

            Feedback();
            Service.SetSimulation(false, 1);
        }

        /// <summary>Fresh measurements of every block (one Track Model observation), with optional changes.</summary>
        public void Feedback(Action<List<TrackModelBlockStateMessage>>? change = null, Func<string, bool>? include = null)
        {
            Clock.Advance(0.01);
            var rows = Service.GetConfiguration().Blocks.Where(block => include?.Invoke(block.BlockId) ?? true).Select(block => new TrackModelBlockStateMessage
            {
                BlockId = block.BlockId,
                ObservedAtUtc = Clock.Now,
                Occupancy = Trains.ContainsKey(block.BlockId) ? OccupancyState.Occupied : OccupancyState.Clear,
                Signal = SignalState.Green,
                Switch = SwitchPosition.Normal,
                Crossing = CrossingState.Closed,
                IsReservedByCtc = false,
            }).ToList();
            change?.Invoke(rows);
            Service.ApplyFeedback(rows);
        }

        /// <summary>The train in <paramref name="from"/> moves into <paramref name="to"/>; both changes in one observation.</summary>
        public void Move(string from, string to, Action<List<TrackModelBlockStateMessage>>? change = null)
        {
            Trains.Remove(from);
            Trains[to] = from;
            Feedback(change);
        }

        public MovementSuggestionMessage Request(string target, string? entry, double authority, params string[] path) => new()
        {
            RequestId = Guid.NewGuid().ToString("N"),
            TrainId = "001",
            TargetBlockId = target,
            EntryBlockId = entry,
            PathBlockIds = [.. path],
            SuggestedSpeedMetersPerSecond = 12,
            SuggestedAuthorityMeters = authority,
        };

        /// <summary>The successor of <paramref name="predecessor"/>: next block, same end point (100 m blocks).</summary>
        public static MovementSuggestionMessage Successor(MovementSuggestionMessage predecessor, double? authority = null) => new()
        {
            RequestId = Guid.NewGuid().ToString("N"),
            TrainId = predecessor.TrainId,
            TargetBlockId = predecessor.PathBlockIds[0],
            EntryBlockId = predecessor.TargetBlockId,
            PathBlockIds = predecessor.PathBlockIds.Skip(1).ToList(),
            SuggestedSpeedMetersPerSecond = predecessor.SuggestedSpeedMetersPerSecond,
            SuggestedAuthorityMeters = authority ?? predecessor.SuggestedAuthorityMeters - 100,
            PredecessorRequestId = predecessor.RequestId,
        };

        public WaysideBlockStatus Block(string id) => Service.GetSnapshot().Blocks.Single(block => block.BlockId == id);

        /// <summary>The authority the wayside codes for the train occupying <paramref name="blockId"/>.</summary>
        public double TrainAuthority(string blockId)
        {
            var block = Block(blockId);
            if (block.Request is not null)
            {
                return block.AuthorizedAuthorityMeters;
            }

            return block.Continuation is { } continuation && Trains.TryGetValue(blockId, out var cameFrom) && cameFrom == continuation.EntryBlockId
                ? continuation.AuthorizedAuthorityMeters
                : 0;
        }

        /// <summary>Every positive grant, with the blocks it covers (100 m blocks).</summary>
        public List<(string RequestId, List<string> Covered)> PositiveGrants() => Service.GetSnapshot().Authorizations
            .Where(block => block.AuthorizedAuthorityMeters > 0)
            .Select(block => (block.Request!.RequestId, new[] { block.Request.TargetBlockId }.Concat(block.Request.PathBlockIds).Take((int)Math.Ceiling(block.AuthorizedAuthorityMeters / 100 - 1e-9)).ToList()))
            .ToList();

        public void AssertNoOverlappingGrants()
        {
            var covered = PositiveGrants().SelectMany(grant => grant.Covered).ToList();
            Assert.AreEqual(covered.Count, covered.Distinct().Count(), "Two positive grants must never cover the same block.");
        }
    }

    /// <summary>Train 001 in block 1 granted 1-2-3 (300 m from block 1's entry), its successor for block 2 staged.</summary>
    private static (Territory Line, MovementSuggestionMessage Predecessor, MovementSuggestionMessage Successor) Prepared(bool hardware, Func<Territory, MovementSuggestionMessage>? request = null)
    {
        var line = new Territory(hardware);
        var predecessor = request?.Invoke(line) ?? line.Request("1", null, 300, "2", "3");
        Assert.AreEqual(MovementSubmission.Accepted, line.Service.SubmitMovement(predecessor));
        var successor = Territory.Successor(predecessor);
        Assert.AreEqual(MovementSubmission.Staged, line.Service.SubmitMovement(successor));
        return (line, predecessor, successor);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StagedSuccessor_GrantsNothing_AndLeavesThePredecessorsGrantUntouched(bool hardware)
    {
        var (line, predecessor, successor) = Prepared(hardware);

        var origin = line.Block("1");
        Assert.AreEqual(predecessor.RequestId, origin.Request!.RequestId);
        Assert.AreEqual(300d, origin.AuthorizedAuthorityMeters, "The staged successor takes part in no conflict check.");
        Assert.HasCount(1, line.Service.GetSnapshot().Authorizations, "A staged successor is not a request the wayside evaluates or reports.");
        Assert.IsNull(line.Block("2").Request);
        Assert.AreEqual(0d, line.Block("2").AuthorizedAuthorityMeters);

        // The next block carries the predecessor's own authority from its entry, for a train from block 1 only.
        var continuation = line.Block("2").Continuation!;
        Assert.AreEqual(predecessor.RequestId, continuation.PredecessorRequestId);
        Assert.AreEqual(successor.RequestId, continuation.SuccessorRequestId);
        Assert.AreEqual("1", continuation.EntryBlockId);
        Assert.AreEqual(200d, continuation.AuthorizedAuthorityMeters, 1e-9, "Same end point: 300 m from block 1's entry is 200 m from block 2's.");
        Assert.AreEqual(12d, continuation.AuthorizedSpeedMetersPerSecond);
        line.AssertNoOverlappingGrants();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Transition_InOneObservation_HandsAuthorityOverAtomically_WithoutAZero(bool hardware)
    {
        var (line, predecessor, successor) = Prepared(hardware);
        Assert.AreEqual(300d, line.TrainAuthority("1"));

        line.Move("1", "2");

        var target = line.Block("2");
        Assert.AreEqual(successor.RequestId, target.Request!.RequestId, "Promoted under its own request ID.");
        Assert.AreEqual(predecessor.RequestId, target.Request.PredecessorRequestId, "The transfer is recorded for diagnostics.");
        Assert.AreEqual(200d, line.TrainAuthority("2"), "Continuous: the train holds the same end point from its new block.");
        Assert.IsNull(line.Block("1").Request, "The predecessor was retired in the same step; it needs no cancellation.");
        Assert.HasCount(1, line.Service.GetSnapshot().Authorizations);
        line.AssertNoOverlappingGrants();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Transition_InSeparateObservations_HandsOverOnEntry_AndTheTrailingClearChangesNothing(bool hardware)
    {
        var (line, _, successor) = Prepared(hardware);

        // The train spans the boundary: block 2 reports occupied first, block 1 is still occupied.
        line.Trains["2"] = "1";
        line.Feedback();
        Assert.AreEqual(successor.RequestId, line.Block("2").Request!.RequestId);
        Assert.AreEqual(200d, line.TrainAuthority("2"));
        line.AssertNoOverlappingGrants();

        // Then block 1 clears.
        line.Trains.Remove("1");
        line.Feedback();
        Assert.AreEqual(successor.RequestId, line.Block("2").Request!.RequestId);
        Assert.AreEqual(200d, line.TrainAuthority("2"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FixedEndPoint_IsCarriedFromBlockToBlock_AndNeverExtended(bool hardware)
    {
        var line = new Territory(hardware);
        var request = line.Request("1", null, 300, "2", "3");
        line.Service.SubmitMovement(request);
        var observed = new List<double> { line.TrainAuthority("1") };

        foreach (var (from, to) in new[] { ("1", "2"), ("2", "3") })
        {
            var successor = Territory.Successor(request);
            Assert.AreEqual(MovementSubmission.Staged, line.Service.SubmitMovement(successor));
            line.Move(from, to);
            observed.Add(line.TrainAuthority(to));
            request = line.Block(to).Request!;
        }

        CollectionAssert.AreEqual(new[] { 300d, 200d, 100d }, observed, "Each block length is subtracted; the 300 m allowance is never restarted.");
        Assert.IsEmpty(line.Block("3").Request!.PathBlockIds, "Authority ends at the original end point (block 3's exit).");
    }

    [TestMethod]
    public void Successor_ThatWouldExtendOrLeaveTheGrantedRoute_IsRejected()
    {
        var line = new Territory(hardware: false);
        var predecessor = line.Request("1", null, 300, "2", "3");
        line.Service.SubmitMovement(predecessor);

        void Rejected(string because, Action<MovementSuggestionMessage> change)
        {
            var successor = Territory.Successor(predecessor);
            change(successor);
            Assert.Throws<ArgumentException>(() => line.Service.SubmitMovement(successor), because);
        }

        Rejected("authority beyond the end point", successor => successor.SuggestedAuthorityMeters = 250);
        Rejected("a route beyond the predecessor's", successor => successor.PathBlockIds = ["3", "4"]);
        Rejected("a higher speed", successor => successor.SuggestedSpeedMetersPerSecond = 15);
        Rejected("not the next block", successor => { successor.TargetBlockId = "3"; successor.EntryBlockId = "2"; successor.PathBlockIds = []; });
        Rejected("not entered from the predecessor's block", successor => successor.EntryBlockId = null);
        Rejected("another train label", successor => successor.TrainId = "002");
        Rejected("an unknown predecessor", successor => successor.PredecessorRequestId = Guid.NewGuid().ToString("N"));

        Assert.IsNull(line.Block("2").Continuation, "Nothing was staged.");
        Assert.AreEqual(300d, line.TrainAuthority("1"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RestrictedGrant_HandsOverOnlyWhatTheWaysideGranted(bool hardware)
    {
        var line = new Territory(hardware);
        line.Trains["3"] = "";               // another train ahead
        line.Feedback();
        var predecessor = line.Request("1", null, 300, "2", "3");
        line.Service.SubmitMovement(predecessor);
        Assert.AreEqual(200d, line.TrainAuthority("1"), "The wayside restricts the authority to end before block 3.");
        line.Service.SubmitMovement(Territory.Successor(predecessor));

        Assert.AreEqual(100d, line.Block("2").Continuation!.AuthorizedAuthorityMeters, "The restriction is carried over, not the request.");
        line.Move("1", "2");
        Assert.AreEqual(100d, line.TrainAuthority("2"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MissingSuccessor_TheTransitionWithdrawsAuthority(bool hardware)
    {
        var line = new Territory(hardware);
        var predecessor = line.Request("1", null, 300, "2", "3");
        line.Service.SubmitMovement(predecessor);
        Assert.IsNull(line.Block("2").Continuation, "Nothing is coded ahead without a prepared successor.");

        line.Move("1", "2");

        Assert.AreEqual(0d, line.TrainAuthority("2"), "No successor: the train is not authorized in block 2.");
        Assert.AreEqual(0d, line.Block("1").AuthorizedAuthorityMeters);
        Assert.Throws<ArgumentException>(() => line.Service.SubmitMovement(predecessor), "Its target cleared: the old request is retired.");
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void DownstreamRestriction_AfterGrant_WithdrawsToZeroInsteadOfShortening(bool hardware, bool closed)
    {
        var (line, _, _) = Prepared(hardware);
        line.Feedback(rows =>
        {
            rows.Single(row => row.BlockId == "1").ActualSpeedMetersPerSecond = 12;
            var obstacle = rows.Single(row => row.BlockId == "3");
            if (closed)
            {
                obstacle.IsClosed = true;
            }
            else
            {
                obstacle.Occupancy = OccupancyState.Occupied;
            }
        });

        Assert.AreEqual(0d, line.TrainAuthority("1"), "Never replace the issued 300 m by 200 m within block 1.");
        Assert.IsNull(line.Block("2").Continuation, "Withdrawal must also remove the prepared continuation.");
        line.Feedback();
        Assert.AreEqual(300d, line.TrainAuthority("1"), "Existing CTC permission becomes eligible again when the safety restriction clears.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ImprovingConditions_KeepIssuedAuthority_AndPreserveItsEndpointAcrossTheBoundary(bool hardware)
    {
        var line = new Territory(hardware);
        line.Trains["3"] = "";
        line.Feedback();
        var request = line.Request("1", null, 300, "2", "3");
        line.Service.SubmitMovement(request);
        Assert.AreEqual(200d, line.TrainAuthority("1"));
        line.Service.SubmitMovement(Territory.Successor(request));

        line.Trains.Remove("3");
        line.Feedback();
        line.Service.SubmitMovement(request);
        Assert.AreEqual(200d, line.TrainAuthority("1"), "Improved conditions and renewals cannot extend an issued grant.");
        Assert.AreEqual(100d, line.Block("2").Continuation!.AuthorizedAuthorityMeters);

        line.Move("1", "2");
        Assert.AreEqual(100d, line.TrainAuthority("2"), "Carry the actual endpoint, not the larger original suggestion.");
        Assert.IsNull(line.Block("1").Request);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CtcReplacement_StartsNewMovement_WhileSpeedOnlyRenewalKeepsIssuedAuthority(bool hardware)
    {
        var line = new Territory(hardware);
        var request = line.Request("1", null, 200, "2");
        line.Service.SubmitMovement(request);
        line.Feedback(rows => rows.Single(row => row.BlockId == "1").ActualSpeedMetersPerSecond = 8);

        request.SuggestedSpeedMetersPerSecond = 6;
        line.Service.SubmitMovement(request);
        Assert.AreEqual(200d, line.TrainAuthority("1"));
        Assert.AreEqual(6d, line.Block("1").AuthorizedSpeedMetersPerSecond);

        var replacement = line.Request("1", null, 300, "2", "3");
        line.Service.SubmitMovement(replacement);
        Assert.AreEqual(300d, line.TrainAuthority("1"), "A new CTC request is a new movement, including a same-block replan.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExplicitCtcLimitChange_IsEvaluatedAsANewMovement(bool hardware)
    {
        var line = new Territory(hardware);
        var request = line.Request("1", null, 300, "2", "3");
        line.Service.SubmitMovement(request);
        request.SuggestedAuthorityMeters = 200;
        line.Service.SubmitMovement(request);
        Assert.AreEqual(200d, line.TrainAuthority("1"), "An explicit CTC limit change is distinct from an autonomous track-condition revision.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CancellationAndReplacement_AreSupportedWithoutStoppedSpeedTelemetry(bool hardware)
    {
        var line = new Territory(hardware);
        var request = line.Request("1", null, 300, "2", "3");
        line.Service.SubmitMovement(request);
        request.IsActive = false;
        line.Service.SubmitMovement(request);
        Assert.AreEqual(0d, line.TrainAuthority("1"), "Cancellation still withdraws permission immediately.");

        foreach (double? speed in new double?[] { null, 12 })
        {
            line.Feedback(rows => rows.Single(row => row.BlockId == "1").ActualSpeedMetersPerSecond = speed);
            line.Service.SubmitMovement(line.Request("1", null, 200, "2"));
            Assert.AreEqual(200d, line.TrainAuthority("1"), "CTC owns replacement timing; optional speed feedback is not a restart condition.");
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StationDwell_DepartureWithoutSpeedTelemetryIncludesFullCurrentBlock_ThenRebasesAtNextEntry(bool hardware)
    {
        var line = new Territory(hardware);
        var arrival = line.Request("1", null, 100);
        line.Service.SubmitMovement(arrival);
        Assert.AreEqual(100d, line.TrainAuthority("1"));

        arrival.SuggestedSpeedMetersPerSecond = 0;
        line.Service.SubmitMovement(arrival);
        Assert.AreEqual(0d, line.TrainAuthority("1"), "Zero authority is allowed during station dwell.");
        line.Feedback();
        Assert.IsNull(line.Block("1").ActualSpeedMetersPerSecond);
        Assert.AreEqual(0d, line.TrainAuthority("1"));

        var departure = line.Request("1", null, 300, "2", "3");
        line.Service.SubmitMovement(departure);
        Assert.AreEqual(300d, line.TrainAuthority("1"), "100 m current block + 200 m onward, with no inferred position offset.");
        line.Service.SubmitMovement(Territory.Successor(departure));
        line.Move("1", "2");
        Assert.AreEqual(200d, line.TrainAuthority("2"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EmergencyRecovery_RechecksSafetyWithoutRequiringSpeedTelemetry(bool hardware)
    {
        var line = new Territory(hardware);
        var request = line.Request("1", null, 300, "2", "3");
        line.Service.SubmitMovement(request);

        line.Trains["3"] = "another train";
        line.Feedback();
        Assert.AreEqual(0d, line.TrainAuthority("1"));
        line.Service.SubmitMovement(request);
        Assert.AreEqual(0d, line.TrainAuthority("1"), "An unchanged renewal cannot bypass a still-unsafe issued grant.");

        line.Trains.Remove("3");
        line.Feedback();
        Assert.IsNull(line.Block("1").ActualSpeedMetersPerSecond);
        Assert.AreEqual(300d, line.TrainAuthority("1"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CtcReplacement_StillWithholdsForFaultsWithoutSpeedTelemetry(bool hardware)
    {
        var line = new Territory(hardware);
        var request = line.Request("1", null, 200, "2");
        line.Service.SubmitMovement(request);
        line.Feedback(rows => rows.Single(row => row.BlockId == "1").BrokenRail = true);
        Assert.AreEqual(0d, line.TrainAuthority("1"));
        request.IsActive = false;
        line.Service.SubmitMovement(request);
        line.Service.SubmitMovement(line.Request("1", null, 300, "2", "3"));
        Assert.AreEqual(0d, line.TrainAuthority("1"), "Accepting CTC's request lifecycle must not bypass a fault.");
        line.Feedback();
        Assert.AreEqual(300d, line.TrainAuthority("1"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DifferentBlockLengths_AreSubtractedInFullAtEachHandoff(bool hardware)
    {
        var configuration = WaysideConfiguration.CreateDemo();
        configuration.Blocks.Single(block => block.BlockId == "1").LengthMeters = 80;
        configuration.Blocks.Single(block => block.BlockId == "2").LengthMeters = 120;
        configuration.Blocks.Single(block => block.BlockId == "3").LengthMeters = 160;
        var line = new Territory(hardware, configuration);
        var first = line.Request("1", null, 360, "2", "3");
        line.Service.SubmitMovement(first);
        var second = Territory.Successor(first, 280);
        line.Service.SubmitMovement(second);
        line.Move("1", "2");
        Assert.AreEqual(280d, line.TrainAuthority("2"));
        line.Service.SubmitMovement(Territory.Successor(second, 160));
        line.Move("2", "3");
        Assert.AreEqual(160d, line.TrainAuthority("3"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RerouteRequestedFromNewBlock_IsEvaluatedFromThatBlocksStart(bool hardware)
    {
        var line = new Territory(hardware);
        var first = line.Request("1", null, 300, "2", "3");
        line.Service.SubmitMovement(first);
        line.Move("1", "2", rows => rows.Single(row => row.BlockId == "2").ActualSpeedMetersPerSecond = 12);
        Assert.AreEqual(0d, line.TrainAuthority("2"), "Without a prepared successor, the new block waits for its request.");

        var rerouted = line.Request("2", "1", 300, "3", "4");
        line.Service.SubmitMovement(rerouted);
        Assert.AreEqual(300d, line.TrainAuthority("2"), "A new block may receive a new endpoint; the old block's grant does not constrain it.");
        first.IsActive = false;
        line.Service.SubmitMovement(first);
        Assert.AreEqual(300d, line.TrainAuthority("2"), "Cleaning up the old request must not cancel the new one.");
    }

    public enum RevocationPoint { BeforeTransition, DuringPreparation, DuringTransfer, AfterTransfer }

    [TestMethod]
    [DataRow(RevocationPoint.BeforeTransition, false)]
    [DataRow(RevocationPoint.BeforeTransition, true)]
    [DataRow(RevocationPoint.DuringPreparation, false)]
    [DataRow(RevocationPoint.DuringPreparation, true)]
    [DataRow(RevocationPoint.DuringTransfer, false)]
    [DataRow(RevocationPoint.DuringTransfer, true)]
    [DataRow(RevocationPoint.AfterTransfer, false)]
    [DataRow(RevocationPoint.AfterTransfer, true)]
    public void GenuineRevocation_WithdrawsAuthorityAtOnce_AndAPreparedSuccessorNeverOverridesIt(RevocationPoint point, bool hardware)
    {
        var line = new Territory(hardware);
        var predecessor = line.Request("1", null, 300, "2", "3");
        line.Service.SubmitMovement(predecessor);
        static void BrokenRailAt3(List<TrackModelBlockStateMessage> rows) => rows.Single(row => row.BlockId == "3").BrokenRail = true;

        switch (point)
        {
            case RevocationPoint.BeforeTransition:
                line.Feedback(BrokenRailAt3);
                Assert.AreEqual(0d, line.TrainAuthority("1"), "Withdrawn by the same evaluation that saw the fault.");
                // CTC prepares only behind a positive grant, but even a successor sent now changes nothing.
                line.Service.SubmitMovement(Territory.Successor(predecessor));
                Assert.IsNull(line.Block("2").Continuation, "A withheld grant is never coded ahead.");
                line.Move("1", "2", BrokenRailAt3);
                break;
            case RevocationPoint.DuringPreparation:
                line.Service.SubmitMovement(Territory.Successor(predecessor));
                Assert.IsNotNull(line.Block("2").Continuation);
                line.Feedback(BrokenRailAt3);
                Assert.AreEqual(0d, line.TrainAuthority("1"));
                Assert.IsNull(line.Block("2").Continuation, "The continuation disappears with the grant it belongs to.");
                line.Move("1", "2", BrokenRailAt3);
                break;
            case RevocationPoint.DuringTransfer:
                line.Service.SubmitMovement(Territory.Successor(predecessor));
                line.Move("1", "2", BrokenRailAt3);
                break;
            case RevocationPoint.AfterTransfer:
                line.Service.SubmitMovement(Territory.Successor(predecessor));
                line.Move("1", "2");
                Assert.AreEqual(200d, line.TrainAuthority("2"));
                line.Feedback(BrokenRailAt3);
                break;
        }

        Assert.AreEqual(0d, line.TrainAuthority("2"), "The fault ahead withdraws all movement authority immediately.");
        Assert.IsEmpty(line.PositiveGrants(), "Nothing anywhere still grants movement.");
        Assert.IsNull(line.Block("3").Continuation);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FaultInTheEnteredBlock_PreventsTheTransfer(bool hardware)
    {
        var (line, _, successor) = Prepared(hardware);

        line.Move("1", "2", rows => rows.Single(row => row.BlockId == "2").TrackCircuitFailure = true);

        Assert.AreEqual(0d, line.TrainAuthority("2"));
        Assert.IsEmpty(line.PositiveGrants());
        Assert.IsFalse(line.Service.GetSnapshot().Authorizations.Any(block => block.Request!.RequestId == successor.RequestId), "Not promoted.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PauseOrMaintenanceMode_WithdrawsTheGrantAndItsContinuation(bool hardware)
    {
        var (line, _, _) = Prepared(hardware);

        line.Service.SetMaintenanceMode(true);
        Assert.AreEqual(0d, line.TrainAuthority("1"));
        Assert.IsNull(line.Block("2").Continuation);

        line.Service.SetMaintenanceMode(false);
        line.Service.SetSimulation(true, 1);
        Assert.AreEqual(0d, line.TrainAuthority("1"));
        Assert.IsNull(line.Block("2").Continuation);
        line.Move("1", "2");
        Assert.AreEqual(0d, line.TrainAuthority("2"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SwitchMisalignedAtTheTransition_WithholdsTheSuccessor(bool hardware)
    {
        var line = new Territory(hardware);
        line.Trains.Clear();
        line.Trains["5"] = "4";
        line.Feedback();
        var predecessor = line.Request("5", "4", 300, "6", "7");
        line.Service.SubmitMovement(predecessor);
        Assert.AreEqual(300d, line.TrainAuthority("5"));
        line.Service.SubmitMovement(Territory.Successor(predecessor));

        // Entering the switch block, the switch reports Reverse (toward 10): the route to 7 is not set.
        line.Move("5", "6", rows => rows.Single(row => row.BlockId == "6").Switch = SwitchPosition.Reverse);

        Assert.AreEqual(0d, line.TrainAuthority("6"));
        Assert.IsEmpty(line.PositiveGrants());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SignalOrCrossingProtectionLost_WithholdsTheSuccessor(bool hardware)
    {
        // Signal: route 2-3-4-5 through the signal at 4, which drops to Red as the train enters 3.
        var line = new Territory(hardware);
        line.Trains.Clear();
        line.Trains["2"] = "1";
        line.Feedback();
        var predecessor = line.Request("2", "1", 400, "3", "4", "5");
        line.Service.SubmitMovement(predecessor);
        Assert.AreEqual(400d, line.TrainAuthority("2"));
        line.Service.SubmitMovement(Territory.Successor(predecessor));
        line.Move("2", "3", rows => rows.Single(row => row.BlockId == "4").Signal = SignalState.Red);
        Assert.AreEqual(0d, line.TrainAuthority("3"), "No proceed aspect at block 4.");
        Assert.IsEmpty(line.PositiveGrants());

        // Crossing: route 7-8-9 through the crossing at 8, which reports open as the train enters 8.
        var crossing = new Territory(hardware);
        crossing.Trains.Clear();
        crossing.Trains["7"] = "6";
        crossing.Feedback();
        var toCrossing = crossing.Request("7", "6", 300, "8", "9");
        crossing.Service.SubmitMovement(toCrossing);
        Assert.AreEqual(300d, crossing.TrainAuthority("7"));
        crossing.Service.SubmitMovement(Territory.Successor(toCrossing));
        crossing.Move("7", "8", rows => rows.Single(row => row.BlockId == "8").Crossing = CrossingState.Open);
        Assert.AreEqual(0d, crossing.TrainAuthority("8"), "The crossing is not protected.");
        Assert.IsEmpty(crossing.PositiveGrants());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StaleFeedback_PreventsTheTransfer(bool hardware)
    {
        var (line, _, successor) = Prepared(hardware);

        // Block 2's measurements stop (everything else stays fresh) until they are older than the watchdog.
        for (int i = 0; i < 6; i++)
        {
            line.Clock.Advance(1);
            line.Feedback(include: id => id != "2");
        }

        Assert.AreEqual(0d, line.TrainAuthority("1"), "Stale feedback inside the route withdraws the grant.");
        Assert.IsNull(line.Block("2").Continuation);

        line.Move("1", "2");
        Assert.AreEqual(0d, line.TrainAuthority("2"), "Block 2 was not observed clear before it was occupied: no transfer.");
        Assert.IsFalse(line.Service.GetSnapshot().Authorizations.Any(block => block.Request!.RequestId == successor.RequestId));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnrenewedRequests_ExpireUnderTheWatchdog_AndCannotBeHandedOver(bool hardware)
    {
        // CTC stops: neither the request nor its successor is renewed.
        var (stopped, _, _) = Prepared(hardware);
        for (int i = 0; i < 11; i++)
        {
            stopped.Clock.Advance(1);
            stopped.Feedback();
        }

        Assert.AreEqual(0d, stopped.TrainAuthority("1"));
        Assert.IsNull(stopped.Block("2").Continuation);
        stopped.Move("1", "2");
        Assert.AreEqual(0d, stopped.TrainAuthority("2"));

        // The request is renewed but its successor is not: the successor expires, so nothing is coded ahead.
        var (renewed, predecessor, _) = Prepared(hardware);
        for (int i = 0; i < 11; i++)
        {
            renewed.Clock.Advance(1);
            renewed.Feedback();
            renewed.Service.SubmitMovement(predecessor);
        }

        Assert.AreEqual(300d, renewed.TrainAuthority("1"));
        Assert.IsNull(renewed.Block("2").Continuation, "An expired successor is discarded.");
        renewed.Move("1", "2");
        Assert.AreEqual(0d, renewed.TrainAuthority("2"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LateRepeatedAndReplayedMessages_CannotRestoreDuplicateOrExtendAuthority(bool hardware)
    {
        var (line, predecessor, successor) = Prepared(hardware);
        line.Move("1", "2");
        var stale = line.Clock.Now.AddSeconds(-0.5);

        // A late renewal of the retired predecessor is refused.
        Assert.Throws<ArgumentException>(() => line.Service.SubmitMovement(predecessor));

        // A late (or repeated) clean-up cancellation of it is harmless.
        var cancellation = line.Request("1", null, 0);
        cancellation.RequestId = predecessor.RequestId;
        cancellation.IsActive = false;
        Assert.AreEqual(MovementSubmission.Cancelled, line.Service.SubmitMovement(cancellation));
        Assert.AreEqual(MovementSubmission.Cancelled, line.Service.SubmitMovement(cancellation));

        // A replayed staging message for the promoted successor is only a renewal of it.
        Assert.AreEqual(MovementSubmission.Accepted, line.Service.SubmitMovement(successor));

        // Neither a renewal nor any other message can move the inherited end point.
        var extended = Territory.Successor(predecessor);
        extended.RequestId = successor.RequestId;
        extended.PredecessorRequestId = null;
        extended.SuggestedAuthorityMeters = 300;
        Assert.Throws<ArgumentException>(() => line.Service.SubmitMovement(extended));

        // No second successor can be prepared behind the retired predecessor.
        Assert.Throws<ArgumentException>(() => line.Service.SubmitMovement(Territory.Successor(predecessor)));

        // An out-of-order observation (older than the last applied) showing the train back in block 1 is ignored.
        line.Service.ApplyFeedback([new TrackModelBlockStateMessage { BlockId = "1", ObservedAtUtc = stale, Occupancy = OccupancyState.Occupied, Signal = SignalState.Green, Switch = SwitchPosition.Normal, Crossing = CrossingState.Closed, IsReservedByCtc = false }]);

        Assert.AreEqual(200d, line.TrainAuthority("2"));
        Assert.AreEqual(successor.RequestId, line.Block("2").Request!.RequestId);
        Assert.IsNull(line.Block("1").Request);
        Assert.HasCount(1, line.PositiveGrants());
        line.AssertNoOverlappingGrants();
    }

    [TestMethod]
    public void CancelledSuccessor_IsRetired_AndAReplacementTakesItsPlace()
    {
        var (line, predecessor, successor) = Prepared(hardware: false);

        var cancellation = Territory.Successor(predecessor);
        cancellation.RequestId = successor.RequestId;
        cancellation.IsActive = false;
        Assert.AreEqual(MovementSubmission.Cancelled, line.Service.SubmitMovement(cancellation));
        Assert.IsNull(line.Block("2").Continuation);
        Assert.Throws<ArgumentException>(() => line.Service.SubmitMovement(successor), "A cancelled successor cannot come back.");

        // A replacement (e.g. after a re-route) replaces the previous one, which is retired.
        var first = Territory.Successor(predecessor, 100);
        var second = Territory.Successor(predecessor, 200);
        line.Service.SubmitMovement(first);
        line.Service.SubmitMovement(second);
        Assert.AreEqual(second.RequestId, line.Block("2").Continuation!.SuccessorRequestId);
        Assert.Throws<ArgumentException>(() => line.Service.SubmitMovement(first));

        // Cancelling the predecessor discards its successor too.
        var cancelPredecessor = line.Request("1", null, 0);
        cancelPredecessor.RequestId = predecessor.RequestId;
        cancelPredecessor.IsActive = false;
        line.Service.SubmitMovement(cancelPredecessor);
        line.Move("1", "2");
        Assert.AreEqual(0d, line.TrainAuthority("2"));
        Assert.IsEmpty(line.PositiveGrants());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AnotherTrainThatCouldHaveEnteredTheBlock_MakesTheTransitionAmbiguous(bool hardware)
    {
        // Make 3 -> 2 a permitted movement, so a train in block 3 could also enter block 2.
        var configuration = WaysideConfiguration.CreateDemo();
        configuration.Blocks.Single(block => block.BlockId == "3").NextBlockIds.Add("2");

        // Train 001 in block 1 (granted into block 2), another train in block 3.
        var line = new Territory(hardware, configuration);
        line.Trains["3"] = "";
        line.Feedback();
        var predecessor = line.Request("1", null, 200, "2");
        line.Service.SubmitMovement(predecessor);
        Assert.AreEqual(200d, line.TrainAuthority("1"));
        line.Service.SubmitMovement(Territory.Successor(predecessor));

        // Block 2 becomes occupied while block 1 stays occupied: either train could be there.
        line.Trains["2"] = "";
        line.Feedback();
        Assert.IsNull(line.Block("2").Request, "No identity is guessed: nothing is handed over.");
        Assert.AreEqual(0d, line.TrainAuthority("2"));

        // Same situation, but the train demonstrably leaves block 1 as block 2 becomes occupied.
        var clear = new Territory(hardware, configuration);
        clear.Trains["3"] = "";
        clear.Feedback();
        var request = clear.Request("1", null, 200, "2");
        clear.Service.SubmitMovement(request);
        var next = Territory.Successor(request);
        clear.Service.SubmitMovement(next);
        clear.Move("1", "2");
        Assert.AreEqual(next.RequestId, clear.Block("2").Request!.RequestId);
        Assert.AreEqual(100d, clear.TrainAuthority("2"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TwoTrains_HandOverIndependently_InOneObservation(bool hardware)
    {
        // Train 001 in block 1 (route 1-2-3); train 002 in block 10 (entered from switch 6, route 10-11-12).
        var line = new Territory(hardware);
        line.Trains["10"] = "6";
        line.Feedback();
        var first = line.Request("1", null, 300, "2", "3");
        var second = line.Request("10", "6", 300, "11", "12");
        second.TrainId = "002";
        second.SuggestedSpeedMetersPerSecond = 8;
        line.Service.SubmitMovement(first);
        line.Service.SubmitMovement(second);
        var firstNext = Territory.Successor(first);
        var secondNext = Territory.Successor(second);
        line.Service.SubmitMovement(firstNext);
        line.Service.SubmitMovement(secondNext);

        line.Trains.Remove("1");
        line.Trains["2"] = "1";
        line.Trains.Remove("10");
        line.Trains["11"] = "10";
        line.Feedback();

        Assert.AreEqual(firstNext.RequestId, line.Block("2").Request!.RequestId);
        Assert.AreEqual(secondNext.RequestId, line.Block("11").Request!.RequestId);
        Assert.AreEqual(200d, line.TrainAuthority("2"));
        Assert.AreEqual(200d, line.TrainAuthority("11"));
        line.AssertNoOverlappingGrants();
    }
}
