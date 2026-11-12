using TrackController.Contracts.Messages;
using TrackController.Core.Services;
using TrackController.Runtime;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Tests;

/// <summary>
/// Regression for the Blue Line signal deadlock: a route through signal 6 or 11 used to be withheld
/// forever ("Waiting for proceed signal feedback at block 6") because the PLC needed a proceed report
/// before granting while the signal was commanded only for granted authority.
/// </summary>
/// <remarks>
/// The track side is <see cref="Line"/>, a stand-in for the Track Model that reports equipment only as
/// it was actually commanded (signals start Red, the crossing starts Open, as in the Track Model). No
/// test sets a signal to a proceed aspect itself.
/// </remarks>
[TestClass]
public sealed class SignalClearanceTests
{
    private const double SpeedLimit = 50 / 3.6;
    private static readonly string[] ToStationB = ["2", "3", "4", "5", "6", "7", "8", "9", "10"];
    private static readonly string[] ToStationC = ["2", "3", "4", "5", "11", "12", "13", "14", "15"];

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }

    /// <summary>The Blue Line controller (real bundled PLC by default) and a Track Model stand-in that echoes commands.</summary>
    private sealed class Line
    {
        private readonly Dictionary<string, TrackModelBlockStateMessage> _track;

        public Clock Clock { get; } = new();

        public TrackControllerService Service { get; }

        public string RequestId { get; private set; } = "";

        public Line(bool hardware = true)
        {
            var configuration = System.Text.Json.JsonSerializer.Deserialize<WaysideConfigurationMessage>(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PiSamples", "blue-line-track-map.json")),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
            Service = new(configuration, Clock);
            if (hardware)
            {
                Service.LoadPlc("wayside.plc", PiRuntimeTests.ProgramSource);
            }
            else
            {
                Service.SetHardwareMode(false);
            }

            // Track Model start-up: signals Red, switch Normal, crossing Open; CTC reservation reported clear.
            _track = configuration.Blocks.ToDictionary(block => block.BlockId, block => new TrackModelBlockStateMessage
            {
                BlockId = block.BlockId,
                Occupancy = OccupancyState.Clear,
                Signal = block.HasSignal ? SignalState.Red : SignalState.Unknown,
                Switch = block.NormalNextBlockId is not null ? SwitchPosition.Normal : SwitchPosition.Unknown,
                Crossing = block.HasCrossing ? CrossingState.Open : CrossingState.Unknown,
                IsReservedByCtc = false
            });
            _track["1"].Occupancy = OccupancyState.Occupied;
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
                    if (block.HasSignal && !track.PowerFailure && block.CommandedSignal is not SignalState.Unknown)
                    {
                        track.Signal = block.CommandedSignal;
                    }

                    if (block.HasSwitch && block.CommandedSwitch is SwitchPosition.Normal or SwitchPosition.Reverse)
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
            Service.ApplyFeedback(_track.Values.Select(row => row with { ObservedAtUtc = Clock.Now, Signal = row.PowerFailure && row.Signal != SignalState.Unknown ? SignalState.Unknown : row.Signal }).ToList());
        }

        public void Submit(string target, string[] path, double authority = 450, double speed = 20)
        {
            RequestId = Guid.NewGuid().ToString("N");
            Service.SubmitMovement(new()
            {
                RequestId = RequestId,
                TrainId = "001",
                TargetBlockId = target,
                PathBlockIds = [.. path],
                SuggestedSpeedMetersPerSecond = speed,
                SuggestedAuthorityMeters = authority
            });
        }

        public WaysideBlockStatus Block(string id) => Service.GetSnapshot().Blocks.Single(block => block.BlockId == id);

        public WaysideBlockStatus Movement => Block("1");

        /// <summary>
        /// Runs Track Model cycles until the movement is granted (at most <paramref name="cycles"/>), checking on
        /// every cycle that authority is never positive unless <paramref name="signal"/> is both commanded and
        /// REPORTED at a proceed aspect.
        /// </summary>
        public List<string> ReportUntilGranted(string signal, int cycles = 4)
        {
            var history = new List<string>();
            for (int i = 0; i <= cycles; i++)
            {
                var movement = Movement;
                var protectedSignal = Block(signal);
                history.Add($"cycle {i}: signal {signal} commanded {protectedSignal.CommandedSignal} reported {protectedSignal.ReportedSignal}; authority {movement.AuthorizedAuthorityMeters} ({movement.MovementReason})");
                if (movement.AuthorizedAuthorityMeters > 0)
                {
                    Assert.IsTrue(IsProceed(protectedSignal.ReportedSignal) && IsProceed(protectedSignal.CommandedSignal), string.Join(Environment.NewLine, history));
                    return history;
                }

                Report();
            }

            Assert.Fail("Authority was never granted:" + Environment.NewLine + string.Join(Environment.NewLine, history));
            return history;
        }
    }

    private static bool IsProceed(SignalState signal) => signal is SignalState.Green or SignalState.SuperGreen or SignalState.Yellow;

    private static void AssertWithheld(Line line, string because)
    {
        var movement = line.Movement;
        Assert.AreEqual(0d, movement.AuthorizedAuthorityMeters, $"{because}: {movement.MovementReason}");
        Assert.AreEqual(0d, movement.AuthorizedSpeedMetersPerSecond, because);
        Assert.AreEqual("Withheld", movement.MovementStatus, because);
    }

    /// <summary>Puts the train on a granted route to Station B through signal 6.</summary>
    private static Line GrantedToStationB(bool hardware = true)
    {
        var line = new Line(hardware);
        line.Submit("1", ToStationB);
        line.ReportUntilGranted("6");
        return line;
    }

    // ---------------------------------------------------------------- Test A: signal 6

    [TestMethod]
    [DataRow(true, DisplayName = "Bundled PLC")]
    [DataRow(false, DisplayName = "Software comparison mode")]
    public void ToStationB_Signal6IsClearedFromASafeRoute_AndAuthorityFollowsItsReportedProceed(bool hardware)
    {
        var line = new Line(hardware);
        Assert.AreEqual(SignalState.Red, line.Block("6").CommandedSignal, "No request, no proceed aspect.");

        line.Submit("1", ToStationB);
        Assert.IsNotNull(line.Movement.Request, "The Track Controller holds the request.");
        AssertWithheld(line, "Nothing is granted before the Track Model reports a proceed aspect");
        StringAssert.Contains(line.Movement.MovementReason, "Waiting for", "A transient wait (crossing or signal) is reported.");

        var history = line.ReportUntilGranted("6");
        Console.WriteLine(string.Join(Environment.NewLine, history));
        Assert.IsTrue(history.Any(entry => entry.Contains("signal 6 commanded Green reported Red", StringComparison.Ordinal)),
            "Signal 6 was commanded to proceed BEFORE authority existed (the prepared route), and the grant waited for its report.");

        var granted = line.Movement;
        Assert.AreEqual(450d, granted.AuthorizedAuthorityMeters);
        Assert.AreEqual(SpeedLimit, granted.AuthorizedSpeedMetersPerSecond, 1e-5, "PLC words have one-millionth resolution.");
        Assert.AreNotEqual("Withheld", granted.MovementStatus);
        Assert.AreEqual(SignalState.Green, line.Block("6").ReportedSignal);
        Assert.AreEqual(SignalState.Red, line.Block("11").CommandedSignal, "The other branch's signal stays Red.");

        // Stable: further cycles keep the same issued authority and aspect.
        line.Report();
        Assert.AreEqual(450d, line.Movement.AuthorizedAuthorityMeters);
        Assert.AreEqual(SignalState.Green, line.Block("6").CommandedSignal);
    }

    [TestMethod]
    public void ToStationB_WithheldMessage_SaysTheProceedAspectIsCommandedAndAwaitingItsReport()
    {
        var line = new Line();
        line.Report(rows => rows["3"].Crossing = CrossingState.Closed); // crossing already confirmed, so only the signal is pending
        line.Submit("1", ToStationB);
        Assert.AreEqual(SignalState.Green, line.Block("6").CommandedSignal);
        Assert.AreEqual(SignalState.Red, line.Block("6").ReportedSignal);
        AssertWithheld(line, "A commanded aspect is not a reported aspect");
        StringAssert.Contains(line.Movement.MovementReason, "Waiting for proceed signal feedback at block 6 (commanded Green; Track Model reports Red)");
    }

    // ---------------------------------------------------------------- Test B: signal 11

    [TestMethod]
    [DataRow(true, DisplayName = "Bundled PLC")]
    [DataRow(false, DisplayName = "Software comparison mode")]
    public void ToStationC_Signal11IsClearedOnceTheSwitchReportsReverse_AndAuthorityFollows(bool hardware)
    {
        var line = new Line(hardware);
        line.Service.RequestSwitch(new() { BlockId = "5", RequestedPosition = SwitchPosition.Reverse });
        line.Submit("1", ToStationC);

        // Commanded but not yet reported reverse: neither signal may clear, nothing is granted.
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("5").CommandedSwitch);
        Assert.AreEqual(SwitchPosition.Normal, line.Block("5").ReportedSwitch);
        Assert.AreEqual(SignalState.Red, line.Block("11").CommandedSignal, "The switch is not proven, so the route is not safe to clear.");
        AssertWithheld(line, "Switch 5 not yet reported reverse");

        var history = line.ReportUntilGranted("11");
        Console.WriteLine(string.Join(Environment.NewLine, history));
        Assert.AreEqual(SwitchPosition.Reverse, line.Block("5").ReportedSwitch);
        Assert.AreEqual(450d, line.Movement.AuthorizedAuthorityMeters);
        Assert.AreEqual(SpeedLimit, line.Movement.AuthorizedSpeedMetersPerSecond, 1e-5, "PLC words have one-millionth resolution.");
        Assert.AreEqual(SignalState.Green, line.Block("11").ReportedSignal);
        Assert.AreEqual(SignalState.Red, line.Block("6").CommandedSignal, "Signal 6 protects the branch this route does not take.");
    }

    // ---------------------------------------------------------------- Test C: unsafe conditions

    [TestMethod]
    [DataRow("power failure at signal 6", 0d)]
    [DataRow("manual red at signal 6", 0d)]
    [DataRow("block 7 feedback unknown", 0d)]
    [DataRow("block 6 broken rail", 0d)]
    [DataRow("switch 5 reported reverse", 0d)]
    [DataRow("Track Controller paused", 0d)]
    [DataRow("conflicting movement", 0d)]
    // An occupied or closed block 6 ends the authority BEFORE the signal (blocks 1-5, 250 m), as on any line.
    [DataRow("block 6 occupied", 250d)]
    [DataRow("maintenance closure of block 6", 250d)]
    public void UnsafeRoute_NeverClearsSignal6_AndNoAuthorityPassesIt(string condition, double maximumAuthority)
    {
        var line = new Line();
        switch (condition)
        {
            case "power failure at signal 6": line.Report(rows => rows["6"].PowerFailure = true); break;
            case "manual red at signal 6":
                line.Service.SetMaintenanceMode(true);
                line.Service.ApplyManual(blockId: "6", signal: SignalState.Red);
                break;
            case "block 7 feedback unknown": line.Report(rows => rows["7"].Occupancy = OccupancyState.Unknown); break;
            case "block 6 broken rail": line.Report(rows => rows["6"].BrokenRail = true); break;
            case "block 6 occupied": line.Report(rows => rows["6"].Occupancy = OccupancyState.Occupied); break;
            case "switch 5 reported reverse": line.Report(rows => rows["5"].Switch = SwitchPosition.Reverse); break;
            case "Track Controller paused": line.Service.SetSimulation(true); break;
            case "maintenance closure of block 6": line.Report(rows => rows["6"].IsClosed = true); break;
            case "conflicting movement":
                line.Report(rows => rows["8"].Occupancy = OccupancyState.Occupied);
                line.Service.SubmitMovement(new() { RequestId = "other", TrainId = "002", TargetBlockId = "8", PathBlockIds = ["9", "10"], SuggestedSpeedMetersPerSecond = 10, SuggestedAuthorityMeters = 100 });
                break;
        }

        line.Submit("1", ToStationB);
        for (int cycle = 0; cycle < 4; cycle++)
        {
            line.Report();
            var movement = line.Movement;
            Assert.IsLessThanOrEqualTo(maximumAuthority, movement.AuthorizedAuthorityMeters, $"{condition}, cycle {cycle}: {movement.MovementReason}");
            Assert.AreEqual(SignalState.Red, line.Block("6").CommandedSignal, $"{condition}, cycle {cycle}: signal 6 stays restrictive.");
            Assert.AreEqual(SignalState.Red, line.Block("11").CommandedSignal, condition);
        }

        if (maximumAuthority == 0)
        {
            AssertWithheld(line, condition);
        }
    }

    [TestMethod]
    public void OccupiedBlockBeyondTheSignal_ClearsSignal6ToCaution_AndAuthorityEndsBeforeTheOccupiedBlock()
    {
        var line = new Line();
        line.Report(rows => rows["8"].Occupancy = OccupancyState.Occupied);
        line.Submit("1", ToStationB);
        line.ReportUntilGranted("6");
        Assert.AreEqual(350d, line.Movement.AuthorizedAuthorityMeters, "Blocks 1-7 only.");
        StringAssert.Contains(line.Movement.MovementReason, "block 8");
        Assert.IsTrue(IsProceed(line.Block("6").ReportedSignal));
    }

    [TestMethod]
    public void PreparedProceedSignal_CannotAuthorizeAMovementThatBecameUnsafeBeforeTheGrant()
    {
        var line = new Line();
        line.Report(rows => rows["3"].Crossing = CrossingState.Closed);
        line.Submit("1", ToStationB);
        Assert.AreEqual(SignalState.Green, line.Block("6").CommandedSignal, "Prepared, not granted.");
        AssertWithheld(line, "Prepared only");

        // The Track Model now reports the proceed aspect, but in the same report block 7 became occupied.
        line.Report(rows => rows["7"].Occupancy = OccupancyState.Occupied);
        Assert.AreEqual(SignalState.Green, line.Block("6").ReportedSignal);
        var movement = line.Movement;
        Assert.IsLessThanOrEqualTo(300d, movement.AuthorizedAuthorityMeters, "Authority ends before block 7, never through it.");
        Assert.AreNotEqual(SignalState.Green, line.Block("6").CommandedSignal, "The prepared Green is re-evaluated to Caution for the shortened route.");

        // Unknown feedback inside the protected route: the prepared aspect is withdrawn and nothing is granted.
        line.Report(rows =>
        {
            rows["7"].Occupancy = OccupancyState.Clear;
            rows["6"].Occupancy = OccupancyState.Unknown;
        });
        AssertWithheld(line, "Unknown block 6");
        Assert.AreEqual(SignalState.Red, line.Block("6").CommandedSignal);
    }

    [TestMethod]
    public void StaleFeedback_WithdrawsThePreparedAspect_AndWithholdsAuthority()
    {
        var line = new Line();
        line.Report(rows => rows["3"].Crossing = CrossingState.Closed);
        line.Submit("1", ToStationB);
        Assert.AreEqual(SignalState.Green, line.Block("6").CommandedSignal);

        // No Track Model report for longer than the feedback watchdog.
        line.Clock.Advance(line.Service.GetConfiguration().FeedbackTimeoutSeconds + 0.5);
        AssertWithheld(line, "Stale feedback");
        Assert.AreEqual(SignalState.Red, line.Block("6").CommandedSignal);
    }

    [TestMethod]
    public void CancelledRequest_LeavesNoPreparedProceedAspect()
    {
        var line = new Line();
        line.Report(rows => rows["3"].Crossing = CrossingState.Closed);
        line.Submit("1", ToStationB);
        Assert.AreEqual(SignalState.Green, line.Block("6").CommandedSignal);

        line.Service.SubmitMovement(new() { RequestId = line.RequestId, TrainId = "001", TargetBlockId = "1", IsActive = false });
        Assert.AreEqual(SignalState.Red, line.Block("6").CommandedSignal);
    }

    [TestMethod]
    public void ExpiredRequest_LeavesNoPreparedProceedAspect()
    {
        var line = new Line();
        line.Report(rows => rows["3"].Crossing = CrossingState.Closed);
        line.Submit("1", ToStationB);
        Assert.AreEqual(SignalState.Green, line.Block("6").CommandedSignal);

        double timeout = line.Service.GetConfiguration().RequestTimeoutSeconds;
        for (double waited = 0; waited <= timeout; waited += 0.5)
        {
            line.Clock.Advance(0.4);
            line.Report();
        }

        StringAssert.Contains(line.Movement.MovementReason, "expired");
        AssertWithheld(line, "Expired request");
        Assert.AreEqual(SignalState.Red, line.Block("6").CommandedSignal);
    }

    [TestMethod]
    public void ReplacementPlcWithoutTheClearanceInput_FailsSafe_ByNeverClearingTheSignal()
    {
        // A custom program that ignores signal_clearance behaves exactly as before: withheld, never forced.
        var line = new Line();
        line.Service.LoadPlc("legacy.plc", PiRuntimeTests.ProgramSource.Replace("((signal_proceed or signal_clearance) and not manual_red)", "(signal_proceed and not manual_red)"));
        line.Service.SetSimulation(false, 1);
        line.Submit("1", ToStationB);
        for (int cycle = 0; cycle < 4; cycle++)
        {
            line.Report();
            AssertWithheld(line, "Legacy PLC");
        }

        Assert.AreEqual(SignalState.Red, line.Block("6").CommandedSignal);
        StringAssert.Contains(line.Movement.MovementReason, "Waiting for proceed signal feedback at block 6");
    }

    // ---------------------------------------------------------------- Test D: revocation

    [TestMethod]
    [DataRow("block 7 occupied")]
    [DataRow("block 6 broken rail")]
    [DataRow("signal 6 power failure")]
    [DataRow("Track Controller paused")]
    [DataRow("conflicting movement")]
    public void AfterTheGrant_AnUnsafeCondition_RevokesAuthorityAndRestrictsSignal6(string condition)
    {
        var line = GrantedToStationB();
        Assert.AreEqual(450d, line.Movement.AuthorizedAuthorityMeters);

        switch (condition)
        {
            case "block 7 occupied": line.Report(rows => rows["7"].Occupancy = OccupancyState.Occupied); break;
            case "block 6 broken rail": line.Report(rows => rows["6"].BrokenRail = true); break;
            case "signal 6 power failure": line.Report(rows => rows["6"].PowerFailure = true); break;
            case "Track Controller paused": line.Service.SetSimulation(true); break;
            case "conflicting movement":
                line.Report(rows => rows["8"].Occupancy = OccupancyState.Occupied);
                line.Service.SubmitMovement(new() { RequestId = "other", TrainId = "002", TargetBlockId = "8", PathBlockIds = ["9", "10"], SuggestedSpeedMetersPerSecond = 10, SuggestedAuthorityMeters = 100 });
                break;
        }

        // The very next evaluation: zero authority (never a shortened nonzero value within the block).
        AssertWithheld(line, condition);
        Assert.AreNotEqual(SignalState.Green, line.Block("6").CommandedSignal, $"{condition}: signal 6 is re-evaluated and no longer Green.");
        if (condition != "block 7 occupied")
        {
            // Block 7 occupied still leaves a safe route into block 6, so Caution is allowed there; everything else is Red.
            Assert.AreEqual(SignalState.Red, line.Block("6").CommandedSignal, $"{condition}: signal 6 is commanded restrictive.");
        }

        // The Track Model applies the restrictive command and reports it; authority stays withdrawn.
        line.Report();
        AssertWithheld(line, condition + " (after the Track Model applied the command)");
    }

    [TestMethod]
    public void AfterTheGrant_LosingSignal6sReportedProceed_RevokesAuthority()
    {
        var line = GrantedToStationB();

        // The Track Model reports signal 6 at Red (e.g. a lamp/aspect fault) although Green is commanded.
        line.Report(rows => rows["6"].Signal = SignalState.Red, applyCommands: false);
        AssertWithheld(line, "Reported Red");
        StringAssert.Contains(line.Movement.MovementReason, "Waiting for proceed signal feedback at block 6");
    }

    // ---------------------------------------------------------------- Unsignalled routes are unchanged

    [TestMethod]
    public void UnsignalledRoute_IsGrantedOnTheFirstCycleAsBefore()
    {
        var line = new Line();
        line.Report(rows => rows["3"].Crossing = CrossingState.Closed);
        line.Submit("1", ["2", "3", "4"], authority: 200);
        Assert.AreEqual(200d, line.Movement.AuthorizedAuthorityMeters, line.Movement.MovementReason);
        Assert.IsTrue(line.Service.GetSnapshot().Blocks.Where(block => block.HasSignal).All(block => block.CommandedSignal == SignalState.Red));
    }
}
