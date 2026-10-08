using TrainControl.Contracts.Enums;
using TrainController.Abstractions.Configuration;
using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;
using TrainController.Core.Services;
using TrainController.Hardware.Pi.Controller;
using static TrainControl.Tests.TrainController.ControllerTestInputs;

namespace TrainControl.Tests.TrainController.Hardware;

/// <summary>
/// Feeds IDENTICAL input vectors to the Software controller (TrainController.Core) and the
/// independently written Hardware controller (Raspberry Pi) and requires equivalent outputs
/// on every tick (numeric tolerance, no bit-equality).
/// </summary>
/// <remarks>
/// A tiny TEST-ONLY kinematic stand-in moves the train from the previous commands so the
/// scenarios naturally pass through acceleration, braking, arrival, dwell, departure and
/// authority exhaustion. It is not part of the product: the Train Controller computes no physics.
/// </remarks>
[TestClass]
public class SoftwareHardwareEquivalenceTests
{
    private const double Tolerance = 1e-9;
    private static readonly EngineerSettings Gains = EngineerSettings.Create(20_000.0, 500.0);

    private delegate (TrainModelInput Model, DriverInput Driver) TickBuilder(int tick, double speed, bool leftOpen, bool rightOpen);

    /// <summary>Runs one scenario on both controllers; returns the Software outputs for scenario-specific asserts.</summary>
    private static List<TrainControllerOutput> RunBoth(string scenario, int ticks, TickBuilder build, ControllerPolicy? policy = null)
    {
        var software = new SoftwareTrainController("TRAIN-002");
        var hardware = new HardwareTrainController("TRAIN-002");
        var outputs = new List<TrainControllerOutput>();
        double speed = 0.0;
        bool left = false, right = false;

        for (var tick = 1; tick <= ticks; tick++)
        {
            var (model, driver) = build(tick, speed, left, right);
            var input = Input(model, driver, tick: tick, trainId: "TRAIN-002", engineer: Gains, policy: policy);

            var sw = software.Step(input);
            var hw = hardware.Step(input);
            AssertEquivalent(sw, hw, $"{scenario}, tick {tick}");
            outputs.Add(sw);

            // Test-only kinematics.
            var c = sw.Commands;
            var acceleration = c.EmergencyBrakeCommand ? -2.73
                : c.ServiceBrakeCommand ? -1.2
                : c.PowerCommandWatts > 0.0 ? 0.5 * Math.Min(1.0, c.PowerCommandWatts / 480_000.0)
                : 0.0;
            speed = Math.Max(0.0, speed + acceleration * Dt);
            left = c.LeftDoorsOpenCommand;
            right = c.RightDoorsOpenCommand;
        }

        return outputs;
    }

    private static TrainModelInput Moving(double speed, double authorized, double authority, bool leftOpen, bool rightOpen,
        BeaconData? beacon = null, bool authorityUpdate = false, bool passenger = false, bool signal = true) =>
        Model(speed: speed, authorized: authorized, authority: authority, passengerEmergency: passenger,
            leftOpen: leftOpen, rightOpen: rightOpen, signalValid: signal, beacon: beacon) with { AuthorityUpdateReceived = authorityUpdate };

    [TestMethod]
    public void Automatic_StationStop_Dwell_Departure_AuthorityCountdown()
    {
        var outputs = RunBoth("automatic", 3_000, (tick, v, l, r) => (
            Moving(v, 15.0, 1_200.0, l, r, beacon: tick == 1 ? Beacon(400.0, "PIONEER", PlatformSide.Left) : null),
            Automatic()));

        Assert.IsTrue(outputs.Any(o => o.Display.StationBrakingActive), "Scenario reached automatic station braking.");
        Assert.IsTrue(outputs.Any(o => o.Display.StationEvent == StationEvent.Arrived));
        Assert.IsTrue(outputs.Any(o => o.Commands.LeftDoorsOpenCommand), "Doors opened on the platform side.");
        Assert.IsTrue(outputs.Any(o => o.Display.StationEvent == StationEvent.Departed));
        Assert.IsTrue(outputs.Any(o => o.Display.AuthorityProtectionActive), "Scenario reached the end of authority.");
    }

    [TestMethod]
    public void Manual_PassThroughStation_ThenAuthorityStop()
    {
        var outputs = RunBoth("manual", 900, (tick, v, l, r) => (
            Moving(v, 20.0, 700.0, l, r, beacon: tick == 1 ? Beacon(300.0, "EDGEBROOK", PlatformSide.Right) : null),
            Manual(requested: 15.0)));

        Assert.IsTrue(outputs.Any(o => o.Display.StationBrakingAdvised));
        Assert.IsTrue(outputs.Any(o => o.Display.StationEvent == StationEvent.PassedWithoutStopping));
        Assert.IsTrue(outputs.Any(o => o.Display.AuthorityProtectionActive), "Scenario reached the end of authority.");
    }

    [TestMethod]
    public void PassengerEmergency_ResetRejectedThenAccepted_AndInvalidInputTick()
    {
        var outputs = RunBoth("passenger", 250, (tick, v, l, r) =>
        {
            var model = Moving(tick == 40 ? double.NaN : v, 15.0, 10_000.0, l, r, passenger: tick is >= 60 and <= 90);
            var driver = Manual(requested: 10.0) with { EmergencyBrakeResetRequested = tick is 80 or 150 };
            return (model, driver);
        });

        Assert.IsTrue(outputs[39].IsFailSafe, "NaN input -> fail-safe on both.");
        Assert.IsTrue(outputs[79].Display.EmergencyBrakeResetRejected);
        Assert.IsFalse(outputs[149].Commands.EmergencyBrakeCommand);
    }

    [TestMethod]
    public void TrackSignalLoss_BothPolicies()
    {
        TickBuilder build = (tick, v, l, r) => (
            Moving(v, 15.0, 10_000.0, l, r, signal: tick is < 50 or > 100),
            Automatic() with { EmergencyBrakeResetRequested = tick == 130 });

        RunBoth("signal/service", 200, build);
        var outputs = RunBoth("signal/emergency", 200, build, ControllerPolicy.Default with { TrackSignalLossResponse = TrackSignalLossResponse.EmergencyBrake });
        Assert.IsTrue(outputs.Any(o => o.Display.EmergencyBrakeCauses.HasFlag(EmergencyBrakeCause.TrackSignalLoss)));
    }

    [TestMethod]
    public void ManualDriverControls_Doors_Lights_ServiceBrake_Announcement_DriverEmergency()
    {
        RunBoth("driver", 300, (tick, v, l, r) =>
        {
            var driver = Manual(requested: tick < 150 ? 0.0 : 8.0) with
            {
                LeftDoorsOpenRequested = tick is >= 10 and < 60 or >= 200 and < 220,
                RightDoorsOpenRequested = tick is >= 30 and < 40,
                ExteriorLightsRequested = tick > 20,
                ServiceBrakeRequested = tick is >= 180 and < 190,
                AnnouncementRequest = tick == 25 ? "Doors closing." : string.Empty,
                EmergencyBrakeRequested = tick == 230,
                EmergencyBrakeResetRequested = tick == 280,
                CabinTemperatureSetpointCelsius = tick < 100 ? 21.0 : 19.5,
            };
            return (Moving(v, 12.0, 10_000.0, l, r), driver);
        });
    }

    [TestMethod]
    public void AuthorityUpdateMidRun_AndBeaconRecalibration()
    {
        RunBoth("authority update", 600, (tick, v, l, r) => (
            Moving(v, 15.0, tick < 100 ? 300.0 : 1_500.0, l, r,
                beacon: tick == 1 ? Beacon(900.0, "WHITED", PlatformSide.Both) : tick == 200 ? Beacon(500.0, "WHITED", PlatformSide.Both) : null,
                authorityUpdate: tick == 100),
            Automatic()));
    }

    [TestMethod]
    public void AuthorityUpdatesDuringSignalLoss_AreIgnoredByBoth()
    {
        // Valid authority 2000 m; signal lost on ticks 50-120 while "updates" of 5000 m and 100 m
        // arrive (both must be ignored); signal back at 121; a trusted update at 140.
        var outputs = RunBoth("signal loss / authority", 300, (tick, v, l, r) => (
            Moving(v, 15.0,
                tick switch { < 60 => 2_000.0, < 80 => 5_000.0, < 140 => 100.0, _ => 1_500.0 },
                l, r,
                authorityUpdate: tick is 1 or 60 or 80 or 140,
                signal: tick is < 50 or > 120),
            Automatic()));

        Assert.IsFalse(outputs[59].Display.AuthorityRecalibrated, "5000 m update ignored (signal invalid).");
        Assert.IsFalse(outputs[79].Display.AuthorityRecalibrated, "100 m update ignored (signal invalid).");
        NumericAssert.GreaterThan(outputs[79].Display.RemainingAuthorityMeters, 100.0, "Trusted estimate kept, not the untrusted 100 m.");
        Assert.IsFalse(outputs[129].Display.AuthorityRecalibrated, "Signal restored without an update: no recalibration.");
        Assert.IsTrue(outputs[139].Display.AuthorityRecalibrated, "Valid-signal update recalibrates.");
        Assert.AreEqual(1_500.0, outputs[139].Display.RemainingAuthorityMeters, 1e-9);
    }

    [TestMethod]
    public void SignalInvalidFromTheStart_NoTrustedAuthority_SameOnBoth()
    {
        var outputs = RunBoth("no trusted authority", 80, (tick, v, l, r) => (
            Moving(v, 15.0, 3_000.0, l, r, authorityUpdate: tick is 1 or 20, signal: tick > 30),
            Automatic()));

        Assert.AreEqual(0.0, outputs[19].Display.RemainingAuthorityMeters, "Update during invalid signal is not trusted.");
        Assert.IsTrue(outputs[30].Display.AuthorityRecalibrated, "First valid-signal tick initializes.");
        Assert.AreEqual(3_000.0, outputs[30].Display.RemainingAuthorityMeters, 1e-9);
    }

    internal static void AssertEquivalent(TrainControllerOutput sw, TrainControllerOutput hw, string context)
    {
        Assert.AreEqual(sw.TrainId, hw.TrainId, context);
        Assert.AreEqual(sw.TickId, hw.TickId, context);
        Assert.AreEqual(sw.IsFailSafe, hw.IsFailSafe, $"{context}: IsFailSafe");

        var a = sw.Commands;
        var b = hw.Commands;
        Assert.AreEqual(a.PowerCommandWatts, b.PowerCommandWatts, 1e-6, $"{context}: power");
        Assert.AreEqual(a.ServiceBrakeCommand, b.ServiceBrakeCommand, $"{context}: service brake");
        Assert.AreEqual(a.EmergencyBrakeCommand, b.EmergencyBrakeCommand, $"{context}: emergency brake");
        Assert.AreEqual(a.LeftDoorsOpenCommand, b.LeftDoorsOpenCommand, $"{context}: left doors");
        Assert.AreEqual(a.RightDoorsOpenCommand, b.RightDoorsOpenCommand, $"{context}: right doors");
        Assert.AreEqual(a.ExteriorLightsCommand, b.ExteriorLightsCommand, $"{context}: lights");
        Assert.AreEqual(a.CabinTemperatureSetpointCelsius, b.CabinTemperatureSetpointCelsius, Tolerance, $"{context}: cabin");
        Assert.AreEqual(a.StationAnnouncement, b.StationAnnouncement, $"{context}: announcement");

        var x = sw.Display;
        var y = hw.Display;
        Assert.AreEqual(x.EffectiveTargetSpeedMetersPerSecond, y.EffectiveTargetSpeedMetersPerSecond, Tolerance, $"{context}: target");
        Assert.AreEqual(x.TargetLimitedBy, y.TargetLimitedBy, $"{context}: limited by");
        Assert.AreEqual(x.RemainingAuthorityMeters, y.RemainingAuthorityMeters, Tolerance, $"{context}: authority");
        AssertNullableClose(x.DistanceToNextStationMeters, y.DistanceToNextStationMeters, $"{context}: station distance");
        AssertNullableClose(x.DistanceToStationBrakePointMeters, y.DistanceToStationBrakePointMeters, $"{context}: brake point");
        AssertNullableClose(x.DistanceToAuthorityBrakePointMeters, y.DistanceToAuthorityBrakePointMeters, $"{context}: authority brake point");
        Assert.AreEqual(x.ServiceBrakeStoppingDistanceMeters, y.ServiceBrakeStoppingDistanceMeters, Tolerance, $"{context}: stopping distance");
        Assert.AreEqual(x.NextStationName, y.NextStationName, $"{context}: station name");
        Assert.AreEqual(x.PlatformSide, y.PlatformSide, $"{context}: platform side");
        Assert.AreEqual(x.StationBrakingAdvised, y.StationBrakingAdvised, $"{context}: advised");
        Assert.AreEqual(x.StationBrakingActive, y.StationBrakingActive, $"{context}: station braking");
        Assert.AreEqual(x.AtStation, y.AtStation, $"{context}: at station");
        Assert.AreEqual(x.StationEvent, y.StationEvent, $"{context}: station event");
        Assert.AreEqual(x.TractionState, y.TractionState, $"{context}: power status");
        Assert.AreEqual(x.AuthorityProtectionActive, y.AuthorityProtectionActive, $"{context}: authority protection");
        Assert.AreEqual(x.OverspeedProtectionActive, y.OverspeedProtectionActive, $"{context}: overspeed");
        Assert.AreEqual(x.DoorInterlockActive, y.DoorInterlockActive, $"{context}: door interlock");
        Assert.AreEqual(x.EmergencyBrakeCauses, y.EmergencyBrakeCauses, $"{context}: EB causes");
        Assert.AreEqual(x.EmergencyBrakeLatched, y.EmergencyBrakeLatched, $"{context}: EB latched");
        Assert.AreEqual(x.EmergencyBrakeResetRejected, y.EmergencyBrakeResetRejected, $"{context}: reset rejected");
        Assert.AreEqual(x.EmergencyBrakeResetBlockedReason, y.EmergencyBrakeResetBlockedReason, $"{context}: reset blocker");
        Assert.AreEqual(x.BeaconRecalibrated, y.BeaconRecalibrated, $"{context}: beacon");
        Assert.AreEqual(x.AuthorityRecalibrated, y.AuthorityRecalibrated, $"{context}: authority update");
        Assert.AreEqual(x.TrackSignalLost, y.TrackSignalLost, $"{context}: signal");
    }

    private static void AssertNullableClose(double? expected, double? actual, string message)
    {
        Assert.AreEqual(expected.HasValue, actual.HasValue, message);
        if (expected is double e && actual is double a)
        {
            Assert.AreEqual(e, a, Tolerance, message);
        }
    }
}
