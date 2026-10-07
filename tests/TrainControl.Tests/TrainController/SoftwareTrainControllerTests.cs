using TrainControl.Contracts.Enums;
using TrainController.Abstractions.Configuration;
using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;
using TrainController.Core.Services;
using static TrainControl.Tests.TrainController.ControllerTestInputs;

namespace TrainControl.Tests.TrainController;

[TestClass]
public class SoftwareTrainControllerTests
{
    private static SoftwareTrainController NewController(string trainId = "TRAIN-001") => new SoftwareTrainController(trainId);

    // ---------------------------------------------------------------- passenger / emergency brake

    [TestMethod]
    public void PassengerEmergencyBrake_AppliesEmergencyBrakeAndZeroPower_WithoutDriverAction()
    {
        var c = NewController();
        var output = Step(c, Input(Model(speed: 10.0, authorized: 15.0, passengerEmergency: true), Manual(requested: 15.0)));

        Assert.IsTrue(output.Commands.EmergencyBrakeCommand);
        Assert.AreEqual(0.0, output.Commands.PowerCommandWatts);
        Assert.IsTrue(output.Display.EmergencyBrakeCauses.HasFlag(EmergencyBrakeCause.Passenger));
        Assert.IsTrue(output.Display.EmergencyBrakeLatched);
    }

    [TestMethod]
    public void PassengerEmergencyBrake_ResetDeniedWhileRequestActive()
    {
        var c = NewController();
        Step(c, Input(Model(speed: 5.0, passengerEmergency: true), tick: 1));

        var output = Step(c, Input(Model(speed: 3.0, passengerEmergency: true),
            new DriverInput { EmergencyBrakeResetRequested = true }, tick: 2));

        Assert.IsTrue(output.Display.EmergencyBrakeResetRejected);
        Assert.IsTrue(output.Commands.EmergencyBrakeCommand);
        Assert.AreEqual(0.0, output.Commands.PowerCommandWatts);
    }

    [TestMethod]
    public void PassengerEmergencyBrake_StaysLatchedAfterRequestClears_ResetThenAllowed()
    {
        var c = NewController();
        Step(c, Input(Model(speed: 5.0, passengerEmergency: true), tick: 1));

        var cleared = Step(c, Input(Model(speed: 0.0), tick: 2));
        Assert.IsTrue(cleared.Commands.EmergencyBrakeCommand, "Clearing the request alone must not release the brake.");

        var reset = Step(c, Input(Model(speed: 0.0), new DriverInput { EmergencyBrakeResetRequested = true }, tick: 3));
        Assert.IsFalse(reset.Commands.EmergencyBrakeCommand);
        Assert.IsFalse(reset.Display.EmergencyBrakeResetRejected);
        Assert.AreEqual(EmergencyBrakeCause.None, c.LatchedEmergencyCauses);
    }

    [TestMethod]
    public void DriverEmergencyBrake_LatchesUntilReset()
    {
        var c = NewController();
        Step(c, Input(Model(speed: 5.0), new DriverInput { EmergencyBrakeRequested = true }, tick: 1));

        Assert.IsTrue(Step(c, Input(Model(speed: 2.0), tick: 2)).Commands.EmergencyBrakeCommand);
        Assert.IsFalse(Step(c, Input(Model(speed: 0.0), new DriverInput { EmergencyBrakeResetRequested = true }, tick: 3)).Commands.EmergencyBrakeCommand);
    }

    [TestMethod]
    public void EmergencyBrake_OneSourceClearingDoesNotRelease_WhileAnotherRemains()
    {
        var c = NewController();
        Step(c, Input(Model(speed: 5.0, passengerEmergency: true), new DriverInput { EmergencyBrakeRequested = true }, tick: 1));

        // Passenger request clears, driver latch remains: still braking without a reset.
        var output = Step(c, Input(Model(speed: 2.0), tick: 2));
        Assert.IsTrue(output.Commands.EmergencyBrakeCommand);
        Assert.IsTrue(output.Display.EmergencyBrakeCauses.HasFlag(EmergencyBrakeCause.Driver));
    }

    [TestMethod]
    public void EmergencyBrakeReset_RejectedWhileAuthorityViolationRemains()
    {
        var c = NewController();
        // 15 m/s needs ~41 m even with the emergency brake; only 10 m of authority remain.
        var first = Step(c, Input(Model(speed: 15.0, authorized: 20.0, authority: 10.0), tick: 1));
        Assert.IsTrue(first.Commands.EmergencyBrakeCommand);
        Assert.IsTrue(first.Display.EmergencyBrakeCauses.HasFlag(EmergencyBrakeCause.AuthorityViolation));

        var reset = Step(c, Input(Model(speed: 12.0, authorized: 20.0, authority: 5.0),
            new DriverInput { EmergencyBrakeResetRequested = true }, tick: 2));
        Assert.IsTrue(reset.Display.EmergencyBrakeResetRejected);
        Assert.IsTrue(reset.Commands.EmergencyBrakeCommand);
    }

    // ---------------------------------------------------------------- beacon / station distance

    [TestMethod]
    public void NewValidBeacon_RecalibratesStationDistance_ThenDeadReckonsWithSpeedTimesDt()
    {
        var c = NewController();

        var t1 = Step(c, Input(Model(speed: 10.0, beacon: Beacon(500.0, "PIONEER", PlatformSide.Left)), tick: 1));
        Assert.IsTrue(t1.Display.BeaconRecalibrated);
        Assert.AreEqual(500.0, t1.Display.DistanceToNextStationMeters!.Value, 1e-9);
        Assert.AreEqual("PIONEER", t1.Display.NextStationName);
        Assert.AreEqual(PlatformSide.Left, t1.Display.PlatformSide);

        // Same beacon data repeated on later ticks (not newly received) must not recalibrate.
        var t2 = Step(c, Input(Model(speed: 10.0, beacon: Beacon(500.0, isNew: false)), tick: 2));
        Assert.IsFalse(t2.Display.BeaconRecalibrated);
        Assert.AreEqual(499.0, t2.Display.DistanceToNextStationMeters!.Value, 1e-9);

        var t3 = Step(c, Input(Model(speed: 8.0), tick: 3));
        Assert.AreEqual(498.2, t3.Display.DistanceToNextStationMeters!.Value, 1e-9);
    }

    [TestMethod]
    public void NewBeacon_OverridesAccumulatedEstimate_AndUpdatesNameAndPlatformSide()
    {
        var c = NewController();
        Step(c, Input(Model(speed: 10.0, beacon: Beacon(500.0, "PIONEER", PlatformSide.Left)), tick: 1));
        for (var tick = 2; tick <= 20; tick++)
        {
            Step(c, Input(Model(speed: 10.0), tick: tick));
        }

        var recalibrated = Step(c, Input(Model(speed: 10.0, beacon: Beacon(300.0, "EDGEBROOK", PlatformSide.Both)), tick: 21));

        Assert.AreEqual(300.0, recalibrated.Display.DistanceToNextStationMeters!.Value, 1e-9);
        Assert.AreEqual("EDGEBROOK", recalibrated.Display.NextStationName);
        Assert.AreEqual(PlatformSide.Both, recalibrated.Display.PlatformSide);
    }

    [TestMethod]
    public void InvalidBeaconReception_IsIgnored()
    {
        var c = NewController();
        var output = Step(c, Input(Model(speed: 10.0, beacon: Beacon(500.0, valid: false)), tick: 1));

        Assert.IsFalse(output.Display.BeaconRecalibrated);
        Assert.IsNull(output.Display.DistanceToNextStationMeters);
    }

    [TestMethod]
    public void ManualMode_PassingStationWithoutStopping_ClearsStation()
    {
        var c = NewController();
        Step(c, Input(Model(speed: 10.0, beacon: Beacon(10.0, "PIONEER")), tick: 1));

        TrainControllerOutput output = null!;
        var passedTick = 0;
        for (var tick = 2; tick <= 20 && passedTick == 0; tick++)
        {
            output = Step(c, Input(Model(speed: 10.0), tick: tick));
            if (output.Display.StationEvent == StationEvent.PassedWithoutStopping)
            {
                passedTick = tick;
            }
        }

        // 10 m at 1 m per tick: cleared once more than the 2 m threshold past the station.
        Assert.AreEqual(14, passedTick);
        Assert.IsNull(output.Display.DistanceToNextStationMeters, "Station icon / distance removed after passing.");
        Assert.IsFalse(output.Display.AtStation);
        Assert.IsTrue(output.Display.Alerts.Any(a => a.Contains("Passed PIONEER")));

        var later = Step(c, Input(Model(speed: 10.0), tick: 30));
        Assert.IsNull(later.Display.DistanceToNextStationMeters, "Stays cleared until the next beacon.");
    }

    [TestMethod]
    public void ManualMode_DepartingAfterStop_ClearsStation()
    {
        var c = NewController();

        var arrived = Step(c, Input(Model(speed: 0.0, beacon: Beacon(1.0, "EDGEBROOK")), tick: 1));
        Assert.IsTrue(arrived.Display.AtStation);
        Assert.AreEqual(StationEvent.Arrived, arrived.Display.StationEvent);

        var waiting = Step(c, Input(Model(speed: 0.0), tick: 2));
        Assert.IsTrue(waiting.Display.AtStation, "Still at the station while stopped.");

        var departed = Step(c, Input(Model(speed: 1.0), Manual(requested: 5.0), tick: 3));
        Assert.AreEqual(StationEvent.Departed, departed.Display.StationEvent);
        Assert.IsNull(departed.Display.DistanceToNextStationMeters);
        Assert.IsFalse(departed.Display.AtStation);
    }

    [TestMethod]
    public void AutomaticMode_DepartingAfterDwell_ClearsStation()
    {
        var c = NewController();
        var policy = ControllerPolicy.Default with { StationDwellTimeSeconds = 0.2 };

        Step(c, Input(Model(speed: 0.0, beacon: Beacon(0.5, side: PlatformSide.Right)), Automatic(), tick: 1, policy: policy));
        Step(c, Input(Model(speed: 0.0), Automatic(), tick: 2, policy: policy));    // dwell complete
        var stillThere = Step(c, Input(Model(speed: 0.0), Automatic(), tick: 3, policy: policy));
        Assert.IsTrue(stillThere.Display.AtStation);

        var departed = Step(c, Input(Model(speed: 0.5), Automatic(), tick: 4, policy: policy));
        Assert.AreEqual(StationEvent.Departed, departed.Display.StationEvent);
        Assert.IsNull(departed.Display.DistanceToNextStationMeters);
        Assert.AreEqual(TargetSpeedConstraint.AuthorizedSpeed, departed.Display.TargetLimitedBy, "No stale station limits the target.");
    }

    [TestMethod]
    public void SlightOvershootWithinThreshold_StillCountsAsArrival()
    {
        var c = NewController();
        Step(c, Input(Model(speed: 10.0, beacon: Beacon(1.0)), tick: 1));        // 1 m to go
        Step(c, Input(Model(speed: 5.0), tick: 2));                              // 0.5 m to go
        var stopped = Step(c, Input(Model(speed: 0.0), tick: 3));                // stopped 0.5 m short: within threshold

        Assert.IsTrue(stopped.Display.AtStation);

        var overshoot = NewController();
        Step(overshoot, Input(Model(speed: 10.0, beacon: Beacon(0.0)), tick: 1));
        var past = Step(overshoot, Input(Model(speed: 15.0), tick: 2));          // 1.5 m past the station
        Assert.AreEqual(-1.5, past.Display.DistanceToNextStationMeters!.Value, 1e-9);
        var stoppedPast = Step(overshoot, Input(Model(speed: 0.0), tick: 3));
        Assert.IsTrue(stoppedPast.Display.AtStation, "Within the 2 m threshold past the station.");
    }

    // ---------------------------------------------------------------- announcements

    [TestMethod]
    public void Announcements_OnceOnNewBeacon_AndOnceOnArrival()
    {
        var c = NewController();

        var beacon = Step(c, Input(Model(speed: 10.0, beacon: Beacon(3.0, "PIONEER", PlatformSide.Left)), tick: 1));
        Assert.AreEqual("Next station: PIONEER. Doors will open on the left.", beacon.Commands.StationAnnouncement);

        var approaching = Step(c, Input(Model(speed: 10.0), tick: 2));
        Assert.AreEqual(string.Empty, approaching.Commands.StationAnnouncement, "Not repeated every tick.");

        var arrived = Step(c, Input(Model(speed: 0.0), tick: 3));
        Assert.IsTrue(arrived.Display.AtStation);
        Assert.AreEqual("Arrived at PIONEER.", arrived.Commands.StationAnnouncement);

        var waiting = Step(c, Input(Model(speed: 0.0), tick: 4));
        Assert.IsTrue(waiting.Display.AtStation);
        Assert.AreEqual(string.Empty, waiting.Commands.StationAnnouncement, "Arrival announced once.");
    }

    [TestMethod]
    [DataRow(PlatformSide.Right, "Next station: WHITED. Doors will open on the right.")]
    [DataRow(PlatformSide.Both, "Next station: WHITED. Doors will open on both sides.")]
    [DataRow(PlatformSide.None, "Next station: WHITED.")]
    public void BeaconAnnouncement_NamesPlatformSide(PlatformSide side, string expected)
    {
        var output = Step(NewController(), Input(Model(speed: 10.0, beacon: Beacon(500.0, "WHITED", side))));

        Assert.AreEqual(expected, output.Commands.StationAnnouncement);
    }

    [TestMethod]
    public void BeaconReadWhileStoppedAtStation_AnnouncesArrival()
    {
        var output = Step(NewController(), Input(Model(speed: 0.0, beacon: Beacon(1.0, "PIONEER"))));

        Assert.AreEqual("Arrived at PIONEER.", output.Commands.StationAnnouncement);
    }

    [TestMethod]
    public void DriverAnnouncement_IsSentOnce_TakesPrecedence_InBothModes()
    {
        foreach (var mode in new[] { OperatingMode.Manual, OperatingMode.Automatic })
        {
            var c = NewController();
            var withBeacon = Step(c, Input(Model(speed: 5.0, beacon: Beacon(500.0)),
                new DriverInput { Mode = mode, AnnouncementRequest = "  Please mind the gap.  " }, tick: 1));
            Assert.AreEqual("Please mind the gap.", withBeacon.Commands.StationAnnouncement, mode.ToString());

            var next = Step(c, Input(Model(speed: 5.0), new DriverInput { Mode = mode }, tick: 2));
            Assert.AreEqual(string.Empty, next.Commands.StationAnnouncement);
        }
    }

    // ---------------------------------------------------------------- remaining authority tracking

    [TestMethod]
    public void RemainingAuthority_DeadReckonsWithSpeedTimesDt_AndRecalibratesOnUpdate()
    {
        var c = NewController();

        var t1 = Step(c, Input(Model(speed: 10.0, authorized: 15.0, authority: 1000.0), tick: 1));
        Assert.AreEqual(1000.0, t1.Display.RemainingAuthorityMeters, 1e-9);
        Assert.IsTrue(t1.Display.AuthorityRecalibrated, "First value received initializes the estimate.");

        var t2 = Step(c, Input(Model(speed: 10.0, authorized: 15.0, authority: 1000.0), tick: 2));
        Assert.AreEqual(999.0, t2.Display.RemainingAuthorityMeters, 1e-9, "Same value without an update: counted down.");
        Assert.IsFalse(t2.Display.AuthorityRecalibrated);

        var t3 = Step(c, Input(Model(speed: 8.0, authorized: 15.0, authority: 1000.0), tick: 3));
        Assert.AreEqual(998.2, t3.Display.RemainingAuthorityMeters, 1e-9);

        var update = Model(speed: 8.0, authorized: 15.0, authority: 500.0) with { AuthorityUpdateReceived = true };
        var t4 = Step(c, Input(update, tick: 4));
        Assert.AreEqual(500.0, t4.Display.RemainingAuthorityMeters, 1e-9, "New authority replaces the estimate.");
        Assert.IsTrue(t4.Display.AuthorityRecalibrated);
    }

    [TestMethod]
    public void SignalInvalid_AuthorityUpdatesIgnored_TrustedEstimateKeepsCountingDown()
    {
        var c = NewController();
        TrainModelInput Tick(double authority, bool update, bool signal) =>
            Model(speed: 10.0, authorized: 15.0, authority: authority, signalValid: signal) with { AuthorityUpdateReceived = update };

        var t1 = Step(c, Input(Tick(1000.0, update: true, signal: true), tick: 1));
        Assert.AreEqual(1000.0, t1.Display.RemainingAuthorityMeters, 1e-9);

        var t2 = Step(c, Input(Tick(5000.0, update: true, signal: false), tick: 2));
        Assert.AreEqual(999.0, t2.Display.RemainingAuthorityMeters, 1e-9, "Update received with invalid signal is NOT trusted.");
        Assert.IsFalse(t2.Display.AuthorityRecalibrated);
        Assert.IsTrue(t2.Display.Alerts.Any(a => a.Contains("Authority update ignored")));

        var t3 = Step(c, Input(Tick(5.0, update: true, signal: false), tick: 3));
        Assert.AreEqual(998.0, t3.Display.RemainingAuthorityMeters, 1e-9, "Not even a smaller value is taken from an invalid signal.");

        var t4 = Step(c, Input(Tick(5000.0, update: false, signal: true), tick: 4));
        Assert.AreEqual(997.0, t4.Display.RemainingAuthorityMeters, 1e-9, "Signal restored without a new update: still counting down.");
        Assert.IsFalse(t4.Display.AuthorityRecalibrated);

        var t5 = Step(c, Input(Tick(700.0, update: true, signal: true), tick: 5));
        Assert.AreEqual(700.0, t5.Display.RemainingAuthorityMeters, 1e-9, "Update with valid signal recalibrates.");
        Assert.IsTrue(t5.Display.AuthorityRecalibrated);
    }

    [TestMethod]
    public void SignalInvalid_BeforeAnyTrustedAuthority_MeansNoAuthority()
    {
        var c = NewController();

        var stopped = Step(c, Input(Model(speed: 0.0, authority: 5000.0, signalValid: false), Automatic(), tick: 1));
        Assert.AreEqual(0.0, stopped.Display.RemainingAuthorityMeters);
        Assert.IsTrue(stopped.Commands.ServiceBrakeCommand, "Held: no trusted authority.");
        Assert.IsFalse(stopped.Commands.EmergencyBrakeCommand);

        var firstValid = Step(c, Input(Model(speed: 0.0, authority: 5000.0), Automatic(), tick: 2));
        Assert.AreEqual(5000.0, firstValid.Display.RemainingAuthorityMeters, 1e-9, "First valid-signal value initializes the estimate.");
        Assert.IsTrue(firstValid.Display.AuthorityRecalibrated);
    }

    [TestMethod]
    public void RemainingAuthority_EstimateDrivesProtection_AndClampsAtZero()
    {
        var c = NewController();
        var gains = ArbitraryTestGains;

        // Train Model keeps reporting 50 m (no update); the estimate shrinks 1 m per tick.
        var firstBrakeTick = 0;
        for (var tick = 1; tick <= 20 && firstBrakeTick == 0; tick++)
        {
            var output = Step(c, Input(Model(speed: 10.0, authorized: 15.0, authority: 50.0), Manual(requested: 10.0), tick: tick, engineer: gains));
            if (output.Display.AuthorityProtectionActive)
            {
                firstBrakeTick = tick;
            }
        }

        // service stop 41.67 m + v·dt 1 m = 42.67 m; estimate after tick n is 50 − (n − 1).
        Assert.AreEqual(9, firstBrakeTick);

        var clamp = NewController();
        Step(clamp, Input(Model(speed: 0.0, authority: 1.0), tick: 1));
        var exhausted = Step(clamp, Input(Model(speed: 20.0, authority: 1.0), tick: 2));
        Assert.AreEqual(0.0, exhausted.Display.RemainingAuthorityMeters);
    }

    // ---------------------------------------------------------------- traction state / reset availability

    [TestMethod]
    public void TractionState_ExplainsZeroPower()
    {
        Assert.AreEqual(TractionState.Powering,
            Step(NewController(), Input(Model(speed: 5.0, authorized: 10.0), Automatic())).Display.TractionState);
        Assert.AreEqual(TractionState.AtOrAboveTarget,
            Step(NewController(), Input(Model(speed: 8.0, authorized: 10.0), Manual(requested: 5.0))).Display.TractionState);
        Assert.AreEqual(TractionState.HoldingStopped,
            Step(NewController(), Input(Model(speed: 0.0, authorized: 10.0), Manual(requested: 0.0))).Display.TractionState);
        Assert.AreEqual(TractionState.ServiceBrake,
            Step(NewController(), Input(Model(speed: 12.0, authorized: 10.0), Automatic())).Display.TractionState);
        Assert.AreEqual(TractionState.DoorInterlock,
            Step(NewController(), Input(Model(speed: 0.0, authorized: 10.0, leftOpen: true), Automatic())).Display.TractionState);
        Assert.AreEqual(TractionState.EmergencyBrake,
            Step(NewController(), Input(Model(speed: 5.0, passengerEmergency: true), Automatic())).Display.TractionState);
    }

    [TestMethod]
    public void EmergencyResetAvailability_IsReportedEveryTick()
    {
        var c = NewController();

        var active = Step(c, Input(Model(speed: 0.0, passengerEmergency: true), tick: 1));
        StringAssert.Contains(active.Display.EmergencyBrakeResetBlockedReason, "passenger");

        var cleared = Step(c, Input(Model(speed: 0.0), tick: 2));
        Assert.IsTrue(cleared.Display.EmergencyBrakeLatched);
        Assert.AreEqual(string.Empty, cleared.Display.EmergencyBrakeResetBlockedReason, "Reset is now possible.");

        var none = Step(NewController(), Input(Model(speed: 0.0)));
        Assert.AreEqual(string.Empty, none.Display.EmergencyBrakeResetBlockedReason);
    }

    // ---------------------------------------------------------------- station braking / arrival

    [TestMethod]
    public void ManualMode_StationBrakingIsAdvisoryOnly()
    {
        var c = NewController();
        // 10 m/s needs 41.7 m with the service brake; station is 40 m away -> braking due.
        var output = Step(c, Input(Model(speed: 10.0, authorized: 15.0, beacon: Beacon(40.0)), Manual(requested: 12.0)));

        Assert.IsTrue(output.Display.StationBrakingAdvised);
        Assert.IsFalse(output.Display.StationBrakingActive);
        Assert.IsFalse(output.Commands.ServiceBrakeCommand, "Manual mode: the driver brakes.");
        NumericAssert.Positive(output.Commands.PowerCommandWatts);
        NumericAssert.AtMost(output.Display.DistanceToStationBrakePointMeters, 0.0);
    }

    [TestMethod]
    public void ManualMode_NoAdvisoryBeforeTheBrakePoint()
    {
        var c = NewController();
        var output = Step(c, Input(Model(speed: 10.0, authorized: 15.0, beacon: Beacon(200.0)), Manual(requested: 12.0)));

        Assert.IsFalse(output.Display.StationBrakingAdvised);
        NumericAssert.GreaterThan(output.Display.DistanceToStationBrakePointMeters, 0.0);
    }

    [TestMethod]
    public void AutomaticMode_AppliesStationBrakingItself()
    {
        var c = NewController();
        var output = Step(c, Input(Model(speed: 10.0, authorized: 15.0, beacon: Beacon(40.0, "WHITED")), Automatic()));

        Assert.IsTrue(output.Display.StationBrakingActive);
        Assert.IsTrue(output.Commands.ServiceBrakeCommand);
        Assert.AreEqual(0.0, output.Commands.PowerCommandWatts);
        Assert.AreEqual(0.0, output.Display.EffectiveTargetSpeedMetersPerSecond);
        Assert.AreEqual("Next station: WHITED. Doors will open on the left.", output.Commands.StationAnnouncement, "Beacon tick announcement.");
    }

    [TestMethod]
    [DataRow(1.5, 0.05, true)]
    [DataRow(1.5, 0.2, false)]  // still moving
    [DataRow(3.0, 0.0, false)]  // outside the distance threshold
    public void Arrival_UsesConfiguredThresholds(double distance, double speed, bool expectedAtStation)
    {
        var c = NewController();
        var output = Step(c, Input(Model(speed: speed, beacon: Beacon(distance))));

        Assert.AreEqual(expectedAtStation, output.Display.AtStation);
    }

    [TestMethod]
    public void Arrival_ThresholdsComeFromPolicy()
    {
        var c = NewController();
        var policy = ControllerPolicy.Default with { StationDistanceThresholdMeters = 5.0 };

        var output = Step(c, Input(Model(speed: 0.0, beacon: Beacon(3.0)), policy: policy));

        Assert.IsTrue(output.Display.AtStation);
    }

    [TestMethod]
    public void AutomaticMode_DwellOpensPlatformSideThenCloses()
    {
        var c = NewController();
        var policy = ControllerPolicy.Default with { StationDwellTimeSeconds = 0.3 };

        var t1 = Step(c, Input(Model(speed: 0.0, beacon: Beacon(1.0, side: PlatformSide.Left)), Automatic(), tick: 1, policy: policy));
        Assert.IsTrue(t1.Display.AtStation);
        Assert.IsTrue(t1.Commands.LeftDoorsOpenCommand);
        Assert.IsFalse(t1.Commands.RightDoorsOpenCommand);
        Assert.IsTrue(t1.Commands.ServiceBrakeCommand, "Held while dwelling.");
        Assert.AreEqual(0.0, t1.Commands.PowerCommandWatts);

        var t2 = Step(c, Input(Model(speed: 0.0, leftOpen: true), Automatic(), tick: 2, policy: policy));
        Assert.IsTrue(t2.Commands.LeftDoorsOpenCommand);

        var t3 = Step(c, Input(Model(speed: 0.0, leftOpen: true), Automatic(), tick: 3, policy: policy));
        Assert.IsFalse(t3.Commands.LeftDoorsOpenCommand, "Dwell complete: doors close.");
        Assert.AreEqual(0.0, t3.Commands.PowerCommandWatts, "Traction inhibited until doors report closed.");

        var t4 = Step(c, Input(Model(speed: 0.0), Automatic(), tick: 4, policy: policy));
        Assert.IsFalse(t4.Commands.LeftDoorsOpenCommand);
        NumericAssert.Positive(t4.Commands.PowerCommandWatts, "Departs once doors are closed.");
    }

    [TestMethod]
    public void AutomaticMode_PlatformBoth_OpensBothSides()
    {
        var c = NewController();
        var output = Step(c, Input(Model(speed: 0.0, beacon: Beacon(0.5, side: PlatformSide.Both)), Automatic()));

        Assert.IsTrue(output.Commands.LeftDoorsOpenCommand);
        Assert.IsTrue(output.Commands.RightDoorsOpenCommand);
    }

    // ---------------------------------------------------------------- authority / overspeed / signal

    [TestMethod]
    public void AuthorityProtection_ServiceBrakeWhenAuthorityNearStoppingDistance()
    {
        var c = NewController();
        // service stop 41.7 m + v·dt 1 m >= 30 m; emergency stop 18.3 m < 30 m.
        var output = Step(c, Input(Model(speed: 10.0, authorized: 15.0, authority: 30.0), Manual(requested: 15.0)));

        Assert.IsTrue(output.Commands.ServiceBrakeCommand);
        Assert.IsFalse(output.Commands.EmergencyBrakeCommand);
        Assert.IsTrue(output.Display.AuthorityProtectionActive);
        Assert.AreEqual(0.0, output.Commands.PowerCommandWatts);
    }

    [TestMethod]
    public void AuthorityProtection_ObeysAuthorityEvenWhenStationIsFarther()
    {
        var c = NewController();
        var output = Step(c, Input(Model(speed: 10.0, authorized: 15.0, authority: 30.0, beacon: Beacon(2000.0)), Automatic()));

        Assert.IsFalse(output.Display.StationBrakingActive);
        Assert.IsTrue(output.Commands.ServiceBrakeCommand);
        Assert.AreEqual(30.0, output.Display.RemainingAuthorityMeters);
        Assert.AreEqual(2000.0, output.Display.DistanceToNextStationMeters!.Value, 1e-9);
    }

    [TestMethod]
    public void AuthorityProtection_HoldsStoppedTrainWithExhaustedAuthority()
    {
        var c = NewController();
        var output = Step(c, Input(Model(speed: 0.0, authorized: 15.0, authority: 0.0), Automatic()));

        Assert.IsTrue(output.Commands.ServiceBrakeCommand);
        Assert.IsFalse(output.Commands.EmergencyBrakeCommand);
        Assert.AreEqual(0.0, output.Commands.PowerCommandWatts);
    }

    [TestMethod]
    public void Overspeed_AppliesServiceBrake()
    {
        var c = NewController();
        var output = Step(c, Input(Model(speed: 12.0, authorized: 10.0), Automatic()));

        Assert.IsTrue(output.Display.OverspeedProtectionActive);
        Assert.IsTrue(output.Commands.ServiceBrakeCommand);
        Assert.AreEqual(0.0, output.Commands.PowerCommandWatts);
    }

    [TestMethod]
    public void TargetSpeed_IsCappedByVehicleMaximum()
    {
        var c = NewController();
        var output = Step(c, Input(Model(speed: 0.0, authorized: 30.0), Automatic()));

        Assert.AreEqual(VehicleSpecification.Flexity2Blackpool.MaxSpeedMetersPerSecond, output.Display.EffectiveTargetSpeedMetersPerSecond, 1e-9);
        Assert.AreEqual(TargetSpeedConstraint.VehicleMaximum, output.Display.TargetLimitedBy);
    }

    [TestMethod]
    public void ManualMode_DriverRequestAboveAuthorizedSpeed_IsLimitedToAuthorized()
    {
        // Driver asks for 30 mph, wayside authorizes only 20 mph.
        var requested = 13.4112; // 30 mph
        var authorized = 8.9408; // 20 mph
        var c = NewController();

        var output = Step(c, Input(Model(speed: 8.9408, authorized: authorized), Manual(requested: requested)));

        Assert.AreEqual(authorized, output.Display.EffectiveTargetSpeedMetersPerSecond, 1e-9);
        Assert.AreEqual(TargetSpeedConstraint.AuthorizedSpeed, output.Display.TargetLimitedBy);
        Assert.AreEqual(0.0, output.Commands.PowerCommandWatts, "Already at the authorized limit: no power toward 30 mph.");
    }

    [TestMethod]
    public void ManualMode_DriverRequestBelowLimits_IsTheBindingConstraint()
    {
        var c = NewController();
        var output = Step(c, Input(Model(authorized: 10.0), Manual(requested: 6.0)));

        Assert.AreEqual(TargetSpeedConstraint.DriverRequest, output.Display.TargetLimitedBy);
    }

    [TestMethod]
    public void RemainingAuthority_LimitsTargetToBrakingCurve_InBothModes()
    {
        // √(2 · 1.2 · 50) = 10.954 m/s < 15 m/s authorized.
        var expected = Math.Sqrt(2.0 * 1.2 * 50.0);

        foreach (var driver in new[] { Manual(requested: 15.0), Automatic() })
        {
            var c = NewController();
            var output = Step(c, Input(Model(speed: 0.0, authorized: 15.0, authority: 50.0), driver));

            Assert.AreEqual(expected, output.Display.EffectiveTargetSpeedMetersPerSecond, 1e-9, driver.Mode.ToString());
            Assert.AreEqual(TargetSpeedConstraint.RemainingAuthority, output.Display.TargetLimitedBy);
        }
    }

    [TestMethod]
    public void AutomaticMode_IgnoresDriverRequestedSpeed()
    {
        var c = NewController();
        var driver = new DriverInput { Mode = OperatingMode.Automatic, RequestedSpeedMetersPerSecond = 3.0 };

        var output = Step(c, Input(Model(speed: 0.0, authorized: 10.0), driver));

        Assert.AreEqual(10.0, output.Display.EffectiveTargetSpeedMetersPerSecond, 1e-9);
        Assert.AreEqual(TargetSpeedConstraint.AuthorizedSpeed, output.Display.TargetLimitedBy);
    }

    [TestMethod]
    public void AutomaticMode_TargetFollowsStationBrakingCurve_ManualDoesNot()
    {
        var expected = Math.Sqrt(2.0 * 1.2 * 50.0); // stop within 50 m

        var automatic = Step(NewController(), Input(Model(speed: 0.0, authorized: 15.0, beacon: Beacon(50.0)), Automatic()));
        Assert.AreEqual(expected, automatic.Display.EffectiveTargetSpeedMetersPerSecond, 1e-9);
        Assert.AreEqual(TargetSpeedConstraint.StationStop, automatic.Display.TargetLimitedBy);

        var manual = Step(NewController(), Input(Model(speed: 0.0, authorized: 15.0, beacon: Beacon(50.0)), Manual(requested: 14.0)));
        Assert.AreEqual(14.0, manual.Display.EffectiveTargetSpeedMetersPerSecond, 1e-9, "Manual: station guidance is advisory only.");
        Assert.AreEqual(TargetSpeedConstraint.DriverRequest, manual.Display.TargetLimitedBy);
    }

    [TestMethod]
    public void AutomaticMode_WithoutValidStationDistance_StationCurveDoesNotApply()
    {
        // No beacon at all, and an invalid beacon reception: neither may imply a zero target.
        var noBeacon = Step(NewController(), Input(Model(speed: 5.0, authorized: 10.0), Automatic()));
        var invalidBeacon = Step(NewController(), Input(Model(speed: 5.0, authorized: 10.0, beacon: Beacon(0.0, valid: false)), Automatic()));

        foreach (var output in new[] { noBeacon, invalidBeacon })
        {
            Assert.AreEqual(10.0, output.Display.EffectiveTargetSpeedMetersPerSecond, 1e-9);
            Assert.AreEqual(TargetSpeedConstraint.AuthorizedSpeed, output.Display.TargetLimitedBy);
            Assert.IsNull(output.Display.DistanceToNextStationMeters);
            Assert.IsFalse(output.Display.StationBrakingActive);
            Assert.IsFalse(output.Display.AtStation);
            Assert.IsFalse(output.Commands.ServiceBrakeCommand);
        }
    }

    [TestMethod]
    public void AuthorityBrakingCurve_AndAuthorityProtection_AreIndependent()
    {
        // 1. Preventive only: curve limits the target while protection is NOT braking.
        var preventive = Step(NewController(), Input(Model(speed: 0.0, authorized: 15.0, authority: 50.0), Automatic()));
        Assert.AreEqual(TargetSpeedConstraint.RemainingAuthority, preventive.Display.TargetLimitedBy);
        Assert.IsFalse(preventive.Display.AuthorityProtectionActive);
        Assert.IsFalse(preventive.Commands.ServiceBrakeCommand);

        // 2. Both: train already faster than the curve allows -> target limited AND backstop brakes.
        var both = Step(NewController(), Input(Model(speed: 10.0, authorized: 15.0, authority: 30.0), Automatic()));
        Assert.AreEqual(Math.Sqrt(2.0 * 1.2 * 30.0), both.Display.EffectiveTargetSpeedMetersPerSecond, 1e-9);
        Assert.AreEqual(TargetSpeedConstraint.RemainingAuthority, both.Display.TargetLimitedBy);
        Assert.IsTrue(both.Display.AuthorityProtectionActive);
        Assert.IsTrue(both.Commands.ServiceBrakeCommand);

        // 3. Backstop beyond the curve: authority shorter than even the emergency stopping distance.
        var backstop = Step(NewController(), Input(Model(speed: 15.0, authorized: 20.0, authority: 10.0), Automatic()));
        Assert.IsTrue(backstop.Commands.EmergencyBrakeCommand);
        Assert.IsTrue(backstop.Display.EmergencyBrakeCauses.HasFlag(EmergencyBrakeCause.AuthorityViolation));
    }

    [TestMethod]
    public void DriverServiceBrake_SetsTargetToZeroWhileHeld_RequestReturnsOnRelease()
    {
        foreach (var mode in new[] { OperatingMode.Manual, OperatingMode.Automatic })
        {
            var c = NewController();

            var braking = Step(c, Input(Model(speed: 8.0, authorized: 15.0),
                new DriverInput { Mode = mode, RequestedSpeedMetersPerSecond = 10.0, ServiceBrakeRequested = true }, tick: 1));
            Assert.AreEqual(0.0, braking.Display.EffectiveTargetSpeedMetersPerSecond, mode.ToString());
            Assert.AreEqual(TargetSpeedConstraint.DriverServiceBrake, braking.Display.TargetLimitedBy);
            Assert.IsTrue(braking.Commands.ServiceBrakeCommand);
            Assert.AreEqual(0.0, braking.Commands.PowerCommandWatts);

            var released = Step(c, Input(Model(speed: 6.0, authorized: 15.0),
                new DriverInput { Mode = mode, RequestedSpeedMetersPerSecond = 10.0 }, tick: 2));
            var expected = mode == OperatingMode.Manual ? 10.0 : 15.0;
            Assert.AreEqual(expected, released.Display.EffectiveTargetSpeedMetersPerSecond, 1e-9, "Target returns on release.");
            Assert.IsFalse(released.Commands.ServiceBrakeCommand);
        }
    }

    [TestMethod]
    public void EmergencyBrake_TakesDisplayPrecedenceOverDriverServiceBrake()
    {
        var output = Step(NewController(), Input(Model(speed: 5.0, passengerEmergency: true),
            new DriverInput { ServiceBrakeRequested = true, RequestedSpeedMetersPerSecond = 5.0 }));

        Assert.AreEqual(TargetSpeedConstraint.EmergencyBrake, output.Display.TargetLimitedBy);
    }

    [TestMethod]
    public void EmergencyBrake_SetsTargetToZero()
    {
        var output = Step(NewController(), Input(Model(speed: 5.0, authorized: 10.0, passengerEmergency: true), Automatic()));

        Assert.AreEqual(0.0, output.Display.EffectiveTargetSpeedMetersPerSecond);
        Assert.AreEqual(TargetSpeedConstraint.EmergencyBrake, output.Display.TargetLimitedBy);
    }

    [TestMethod]
    public void ManualMode_TargetIsDriverRequestCappedByAuthorizedSpeed()
    {
        var c = NewController();

        Assert.AreEqual(6.0, Step(c, Input(Model(authorized: 10.0), Manual(requested: 6.0))).Display.EffectiveTargetSpeedMetersPerSecond);
        Assert.AreEqual(10.0, Step(c, Input(Model(authorized: 10.0), Manual(requested: 14.0), tick: 2)).Display.EffectiveTargetSpeedMetersPerSecond);
    }

    [TestMethod]
    public void TrackSignalLost_Default_StopsWithServiceBrake_NotEmergency()
    {
        Assert.AreEqual(TrackSignalLossResponse.ServiceBrakeStop, ControllerPolicy.Default.TrackSignalLossResponse);

        var c = NewController();
        Step(c, Input(Model(speed: 5.0, authorized: 15.0), Automatic(), tick: 0)); // trusted authority first
        var output = Step(c, Input(Model(speed: 5.0, authorized: 15.0, signalValid: false), Automatic()));

        Assert.IsTrue(output.Display.TrackSignalLost);
        Assert.AreEqual(0.0, output.Display.EffectiveTargetSpeedMetersPerSecond);
        Assert.AreEqual(TargetSpeedConstraint.TrackSignalLoss, output.Display.TargetLimitedBy);
        Assert.IsTrue(output.Commands.ServiceBrakeCommand);
        Assert.IsFalse(output.Commands.EmergencyBrakeCommand);
        Assert.AreEqual(0.0, output.Commands.PowerCommandWatts);

        var stopped = Step(c, Input(Model(speed: 0.0, authorized: 15.0, signalValid: false), Automatic(), tick: 2));
        Assert.IsTrue(stopped.Commands.ServiceBrakeCommand, "Held once stopped.");

        var restored = Step(c, Input(Model(speed: 0.0, authorized: 15.0), Automatic(), tick: 3));
        Assert.IsFalse(restored.Commands.ServiceBrakeCommand, "Service-brake response clears when the signal returns.");
        NumericAssert.Positive(restored.Commands.PowerCommandWatts);
    }

    [TestMethod]
    public void TrackSignalLost_ConfiguredEmergency_LatchesAndRefusesResetWhileLost()
    {
        var c = NewController();
        var policy = ControllerPolicy.Default with { TrackSignalLossResponse = TrackSignalLossResponse.EmergencyBrake };
        Step(c, Input(Model(speed: 5.0, authorized: 15.0), Automatic(), tick: 0, policy: policy)); // trusted authority first

        var lost = Step(c, Input(Model(speed: 5.0, authorized: 15.0, signalValid: false), Automatic(), tick: 1, policy: policy));
        Assert.IsTrue(lost.Commands.EmergencyBrakeCommand);
        Assert.IsTrue(lost.Display.EmergencyBrakeCauses.HasFlag(EmergencyBrakeCause.TrackSignalLoss));
        Assert.AreEqual(0.0, lost.Commands.PowerCommandWatts);

        var refused = Step(c, Input(Model(speed: 0.0, authorized: 15.0, signalValid: false),
            new DriverInput { Mode = OperatingMode.Automatic, EmergencyBrakeResetRequested = true }, tick: 2, policy: policy));
        Assert.IsTrue(refused.Display.EmergencyBrakeResetRejected);
        Assert.IsTrue(refused.Commands.EmergencyBrakeCommand);

        var stillLatched = Step(c, Input(Model(speed: 0.0, authorized: 15.0), Automatic(), tick: 3, policy: policy));
        Assert.IsTrue(stillLatched.Commands.EmergencyBrakeCommand, "Signal returning alone does not release a latched emergency brake.");

        var reset = Step(c, Input(Model(speed: 0.0, authorized: 15.0),
            new DriverInput { Mode = OperatingMode.Automatic, EmergencyBrakeResetRequested = true }, tick: 4, policy: policy));
        Assert.IsFalse(reset.Commands.EmergencyBrakeCommand);
    }

    [TestMethod]
    public void UndefinedPolicyEnum_IsRejectedAsInvalidInput()
    {
        var c = NewController();
        var policy = ControllerPolicy.Default with { TrackSignalLossResponse = (TrackSignalLossResponse)99 };

        var output = c.Step(Input(Model(), policy: policy));

        Assert.IsTrue(output.IsFailSafe);
        Assert.IsTrue(output.Display.EmergencyBrakeCauses.HasFlag(EmergencyBrakeCause.InvalidInput));
    }

    // ---------------------------------------------------------------- PI

    [TestMethod]
    public void PI_ProportionalTerm_UsesSiSpeedError()
    {
        var c = NewController();
        var output = Step(c, Input(Model(speed: 5.0, authorized: 10.0), Automatic(), engineer: EngineerSettings.Create(1000.0, 0.0)));

        Assert.AreEqual(5000.0, output.Commands.PowerCommandWatts, 1e-9);
    }

    [TestMethod]
    public void PI_IntegralTerm_IntegratesWithSimulationDeltaTime()
    {
        var c = NewController();
        var gains = EngineerSettings.Create(1000.0, 100.0);

        var t1 = Step(c, Input(Model(speed: 5.0, authorized: 10.0), Automatic(), tick: 1, engineer: gains));
        Assert.AreEqual(5000.0 + 100.0 * 0.5, t1.Commands.PowerCommandWatts, 1e-9);

        var t2 = Step(c, Input(Model(speed: 5.0, authorized: 10.0), Automatic(), tick: 2, engineer: gains));
        Assert.AreEqual(5000.0 + 100.0 * 1.0, t2.Commands.PowerCommandWatts, 1e-9);

        var other = NewController();
        Step(other, Input(Model(speed: 5.0, authorized: 10.0), Automatic(), engineer: gains, dt: 0.5));
        Assert.AreEqual(2.5, other.IntegralMeters, 1e-9, "Integral uses the input's dt, not wall-clock time.");
    }

    [TestMethod]
    public void PI_OutputIsClampedToRatedPower_WithoutIntegralWindup()
    {
        var c = NewController();
        var output = Step(c, Input(Model(speed: 0.0, authorized: 15.0), Automatic(), engineer: EngineerSettings.Create(1e6, 1e5)));

        Assert.AreEqual(480_000.0, output.Commands.PowerCommandWatts, 1e-6);
        Assert.AreEqual(0.0, c.IntegralMeters);
    }

    [TestMethod]
    public void PI_NeverCommandsNegativePower()
    {
        var c = NewController();
        var output = Step(c, Input(Model(speed: 8.0, authorized: 10.0), Manual(requested: 5.0), engineer: EngineerSettings.Create(1000.0, 0.0)));

        Assert.AreEqual(0.0, output.Commands.PowerCommandWatts);
        Assert.IsFalse(output.Commands.ServiceBrakeCommand, "Below the authorized limit the controller coasts; the driver may brake.");
    }

    [TestMethod]
    public void PI_IntegralClearedWhileBraking()
    {
        var c = NewController();
        var gains = EngineerSettings.Create(10.0, 10.0);
        Step(c, Input(Model(speed: 5.0, authorized: 10.0), Automatic(), tick: 1, engineer: gains));
        NumericAssert.Positive(c.IntegralMeters);

        Step(c, Input(Model(speed: 5.0, authorized: 10.0), new DriverInput { Mode = OperatingMode.Automatic, ServiceBrakeRequested = true }, tick: 2, engineer: gains));
        Assert.AreEqual(0.0, c.IntegralMeters);
    }

    // ---------------------------------------------------------------- doors

    [TestMethod]
    public void Doors_OpenRequestWhileMoving_IsRefused()
    {
        var c = NewController();
        var output = Step(c, Input(Model(speed: 5.0, authorized: 10.0), new DriverInput { RequestedSpeedMetersPerSecond = 8.0, LeftDoorsOpenRequested = true }));

        Assert.IsFalse(output.Commands.LeftDoorsOpenCommand);
        Assert.IsTrue(output.Display.DoorInterlockActive);
    }

    [TestMethod]
    public void Doors_ManualStopped_FollowDriver_AndInhibitTraction()
    {
        var c = NewController();
        var output = Step(c, Input(Model(speed: 0.0, authorized: 10.0), new DriverInput { RequestedSpeedMetersPerSecond = 8.0, RightDoorsOpenRequested = true }));

        Assert.IsTrue(output.Commands.RightDoorsOpenCommand);
        Assert.IsFalse(output.Commands.LeftDoorsOpenCommand);
        Assert.AreEqual(0.0, output.Commands.PowerCommandWatts);
    }

    [TestMethod]
    public void Doors_ReportedOpen_InhibitTraction()
    {
        var c = NewController();
        var output = Step(c, Input(Model(speed: 0.0, authorized: 10.0, leftOpen: true), Automatic()));

        Assert.IsTrue(output.Display.DoorInterlockActive);
        Assert.AreEqual(0.0, output.Commands.PowerCommandWatts);
    }

    // ---------------------------------------------------------------- equipment / identity / reset

    [TestMethod]
    public void LightsAndCabinSetpoint_FollowDriver()
    {
        var c = NewController();
        var output = Step(c, Input(Model(), new DriverInput { ExteriorLightsRequested = true, CabinTemperatureSetpointCelsius = 18.5 }));

        Assert.IsTrue(output.Commands.ExteriorLightsCommand);
        Assert.AreEqual(18.5, output.Commands.CabinTemperatureSetpointCelsius);
    }

    [TestMethod]
    public void Output_EchoesTrainIdAndTickId_AndRejectsOtherTrainsInput()
    {
        var c = NewController("TRAIN-003");
        var output = Step(c, Input(Model(), trainId: "TRAIN-003", tick: 42));

        Assert.AreEqual("TRAIN-003", output.TrainId);
        Assert.AreEqual(42L, output.TickId);
        Assert.ThrowsExactly<ArgumentException>(() => c.Step(Input(Model(), trainId: "TRAIN-001")));
    }

    [TestMethod]
    public void InvalidInput_ProducesFailSafe()
    {
        var c = NewController();
        var output = c.Step(Input(Model(speed: double.NaN)));

        Assert.IsTrue(output.IsFailSafe);
        Assert.IsTrue(output.Commands.EmergencyBrakeCommand);
        Assert.AreEqual(0.0, output.Commands.PowerCommandWatts);
        Assert.IsTrue(output.Display.EmergencyBrakeCauses.HasFlag(EmergencyBrakeCause.InvalidInput));
    }

    [TestMethod]
    public void Reset_ClearsAllRuntimeState()
    {
        var c = NewController();
        var gains = EngineerSettings.Create(10.0, 10.0);
        Step(c, Input(Model(speed: 5.0, authorized: 10.0, beacon: Beacon(300.0)), Automatic(), tick: 1, engineer: gains));
        Step(c, Input(Model(speed: 5.0, authorized: 10.0, passengerEmergency: true), Automatic(), tick: 2, engineer: gains));

        c.Reset();

        Assert.AreEqual(0.0, c.IntegralMeters);
        Assert.AreEqual(EmergencyBrakeCause.None, c.LatchedEmergencyCauses);
        Assert.IsNull(c.LastOutput);
        var after = Step(c, Input(Model(speed: 0.0, authorized: 10.0), Automatic(), tick: 1, engineer: gains));
        Assert.IsNull(after.Display.DistanceToNextStationMeters);
        Assert.IsFalse(after.Commands.EmergencyBrakeCommand);
    }

    [TestMethod]
    public void Controllers_DoNotShareState()
    {
        var a = NewController("TRAIN-001");
        var b = NewController("TRAIN-003");
        var gains = EngineerSettings.Create(10.0, 10.0);

        Step(a, Input(Model(speed: 5.0, authorized: 10.0, passengerEmergency: true, beacon: Beacon(300.0)), Automatic(), trainId: "TRAIN-001", engineer: gains));
        var bOut = Step(b, Input(Model(speed: 5.0, authorized: 10.0), Automatic(), trainId: "TRAIN-003", engineer: gains));

        Assert.AreEqual(EmergencyBrakeCause.Passenger, a.LatchedEmergencyCauses);
        Assert.AreEqual(EmergencyBrakeCause.None, b.LatchedEmergencyCauses);
        Assert.AreEqual(0.0, a.IntegralMeters);
        Assert.AreEqual(0.5, b.IntegralMeters, 1e-9);
        Assert.IsNull(bOut.Display.DistanceToNextStationMeters);
    }
}
