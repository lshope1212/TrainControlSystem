using TrainController.Abstractions.Configuration;
using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;
using TrainController.Core.Services;
using static TrainControl.Tests.TrainController.ControllerTestInputs;

namespace TrainControl.Tests.TrainController;

/// <summary>
/// Signal Pickup / Brake / Engine failures: inferred from observable data only, latched, cleared
/// only by "Reset failures" when each reset condition holds (dt = 0.1 s in all tests).
/// </summary>
[TestClass]
public class FailureDetectionTests
{
    private static SoftwareTrainController NewController() => new SoftwareTrainController("TRAIN-001");

    private static DriverInput ResetFailures(DriverInput? driver = null) => (driver ?? Automatic()) with { ResetFailuresRequested = true };

    // ---------------------------------------------------------------- signal pickup

    [TestMethod]
    public void InvalidSignal_ImmediatelyStopsWithServiceBrake_BeforeAnyFailureLatches()
    {
        var c = NewController();
        Step(c, Input(Model(speed: 5.0, commanded: 15.0), Automatic(), tick: 1)); // trusted authority

        var lost = Step(c, Input(Model(speed: 5.0, commanded: 15.0, signalValid: false), Automatic(), tick: 2));

        Assert.AreEqual(0.0, lost.Display.SetpointSpeedMetersPerSecond);
        Assert.AreEqual(TargetSpeedConstraint.TrackSignalLoss, lost.Display.TargetLimitedBy);
        Assert.IsTrue(lost.Commands.ServiceBrakeCommand);
        Assert.IsFalse(lost.Commands.EmergencyBrakeCommand);
        Assert.AreEqual(0.0, lost.Commands.PowerCommandWatts);
        Assert.AreEqual(ControllerFailure.None, lost.Display.Failures, "Not latched after a single tick.");
    }

    [TestMethod]
    public void SignalPickupFailure_LatchesAfterHalfASecond()
    {
        var c = NewController();
        Step(c, Input(Model(speed: 0.0, commanded: 15.0), Automatic(), tick: 1));

        TrainControllerOutput output = null!;
        for (var tick = 2; tick <= 5; tick++)
        {
            output = Step(c, Input(Model(speed: 0.0, commanded: 15.0, signalValid: false), Automatic(), tick: tick));
        }

        Assert.AreEqual(ControllerFailure.None, output.Display.Failures, "4 ticks = 0.4 s: not yet.");

        output = Step(c, Input(Model(speed: 0.0, commanded: 15.0, signalValid: false), Automatic(), tick: 6));
        Assert.AreEqual(ControllerFailure.SignalPickup, output.Display.Failures, "5 ticks = 0.5 s: latched.");
    }

    [TestMethod]
    public void SignalPickupFailure_ShortGlitchRecoversWithoutReset()
    {
        var c = NewController();
        Step(c, Input(Model(speed: 0.0, commanded: 15.0), Automatic(), tick: 1));
        for (var tick = 2; tick <= 4; tick++)
        {
            Step(c, Input(Model(speed: 0.0, commanded: 15.0, signalValid: false), Automatic(), tick: tick));
        }

        var back = Step(c, Input(Model(speed: 0.0, commanded: 15.0), Automatic(), tick: 5));
        Assert.AreEqual(ControllerFailure.None, back.Display.Failures);
        NumericAssert.Positive(back.Commands.PowerCommandWatts, "Glitch shorter than 0.5 s: resumes immediately.");
    }

    [TestMethod]
    public void SignalPickupFailure_StaysLatchedWhenSignalReturns_ResetRejectedWhileInvalid_AcceptedWhenValid()
    {
        var c = NewController();
        Step(c, Input(Model(speed: 0.0, commanded: 15.0), Automatic(), tick: 1));
        for (var tick = 2; tick <= 6; tick++)
        {
            Step(c, Input(Model(speed: 0.0, commanded: 15.0, signalValid: false), Automatic(), tick: tick));
        }

        var rejected = Step(c, Input(Model(speed: 0.0, commanded: 15.0, signalValid: false), ResetFailures(), tick: 7));
        Assert.IsTrue(rejected.Display.FailureResetRejected);
        StringAssert.Contains(rejected.Display.FailureResetRejectedReason, "signal");
        Assert.AreEqual(ControllerFailure.SignalPickup, rejected.Display.Failures);

        var held = Step(c, Input(Model(speed: 0.0, commanded: 15.0), Automatic(), tick: 8));
        Assert.AreEqual(ControllerFailure.SignalPickup, held.Display.Failures, "Signal back alone does not clear the latch.");
        Assert.AreEqual(0.0, held.Display.SetpointSpeedMetersPerSecond);
        Assert.AreEqual(TargetSpeedConstraint.Failure, held.Display.TargetLimitedBy);
        Assert.IsTrue(held.Commands.ServiceBrakeCommand);
        Assert.AreEqual(string.Empty, held.Display.FailureResetBlockedReason, "A reset would be accepted now.");

        var reset = Step(c, Input(Model(speed: 0.0, commanded: 15.0), ResetFailures(), tick: 9));
        Assert.AreEqual(ControllerFailure.None, reset.Display.Failures);
        Assert.IsFalse(reset.Display.FailureResetRejected);
        NumericAssert.Positive(reset.Commands.PowerCommandWatts, "Resumes after the reset.");
    }

    [TestMethod]
    public void InvalidSignal_DoesNotTrustNewCommandedSpeedOrAuthority()
    {
        var c = NewController();
        Step(c, Input(Model(speed: 10.0, commanded: 15.0, authority: 2_000.0), Automatic(), tick: 1));

        var lost = Step(c, Input(Model(speed: 10.0, commanded: 30.0, authority: 5_000.0, signalValid: false) with { AuthorityUpdateReceived = true },
            Automatic(), tick: 2));

        Assert.AreEqual(0.0, lost.Display.SetpointSpeedMetersPerSecond, "The new commanded speed is not used.");
        Assert.IsFalse(lost.Display.AuthorityRecalibrated);
        Assert.AreEqual(1_999.0, lost.Display.RemainingAuthorityMeters, 1e-9, "Trusted estimate dead-reckoned: 2000 − 10·0.1.");
    }

    // ---------------------------------------------------------------- brake failure

    [TestMethod]
    public void BrakeFailure_NormalServiceBrakeDeceleration_DoesNotTrigger()
    {
        var c = NewController();
        var braking = new DriverInput { ServiceBrakeRequested = true };
        var speed = 10.0;
        for (var tick = 1; tick <= 30; tick++)
        {
            var output = Step(c, Input(Model(speed: speed, commanded: 15.0), braking, tick: tick));
            Assert.AreEqual(ControllerFailure.None, output.Display.Failures, $"tick {tick}");
            speed = Math.Max(0.0, speed - 1.2 * Dt);
        }
    }

    [TestMethod]
    public void BrakeFailure_InsufficientDecelerationForOneSecond_TriggersEmergencyBrake()
    {
        var c = NewController();
        var braking = new DriverInput { ServiceBrakeRequested = true };
        TrainControllerOutput output = null!;

        // Service brake commanded from tick 1; speed does not drop. Window = ticks 2..11 (1.0 s).
        for (var tick = 1; tick <= 10; tick++)
        {
            output = Step(c, Input(Model(speed: 10.0, commanded: 15.0), braking, tick: tick));
        }

        Assert.AreEqual(ControllerFailure.None, output.Display.Failures, "0.9 s: not yet.");

        output = Step(c, Input(Model(speed: 10.0, commanded: 15.0), braking, tick: 11));
        Assert.AreEqual(ControllerFailure.Brake, output.Display.Failures);
        Assert.IsTrue(output.Commands.EmergencyBrakeCommand, "Brake failure → emergency brake.");
        Assert.IsTrue(output.Display.EmergencyBrakeCauses.HasFlag(EmergencyBrakeCause.BrakeFailure));
        Assert.AreEqual(0.0, output.Commands.PowerCommandWatts);
        Assert.AreEqual(0.0, output.Display.SetpointSpeedMetersPerSecond);
    }

    [TestMethod]
    public void BrakeFailure_SlightDecelerationBelowThreshold_StillTriggers()
    {
        var c = NewController();
        var braking = new DriverInput { ServiceBrakeRequested = true };
        var speed = 10.0;
        TrainControllerOutput output = null!;
        for (var tick = 1; tick <= 11; tick++)
        {
            output = Step(c, Input(Model(speed: speed, commanded: 15.0), braking, tick: tick));
            speed -= 0.2 * Dt; // 0.2 m/s² < 0.3 m/s²
        }

        Assert.AreEqual(ControllerFailure.Brake, output.Display.Failures);
    }

    [TestMethod]
    public void BrakeFailure_ResetRejectedWhileMoving_AcceptedWhenStopped_DetectedAgainLater()
    {
        var c = NewController();
        var braking = new DriverInput { ServiceBrakeRequested = true };
        for (var tick = 1; tick <= 11; tick++)
        {
            Step(c, Input(Model(speed: 10.0, commanded: 15.0), braking, tick: tick));
        }

        var moving = Step(c, Input(Model(speed: 6.0, commanded: 15.0), ResetFailures(), tick: 12));
        Assert.IsTrue(moving.Display.FailureResetRejected);
        StringAssert.Contains(moving.Display.FailureResetRejectedReason, "moving");
        Assert.IsTrue(moving.Commands.EmergencyBrakeCommand);

        var stopped = Step(c, Input(Model(speed: 0.0, commanded: 15.0), ResetFailures(), tick: 13));
        Assert.AreEqual(ControllerFailure.None, stopped.Display.Failures);
        Assert.IsFalse(stopped.Commands.EmergencyBrakeCommand, "No other emergency cause: released by Reset failures.");

        // Fails again later: detected again.
        TrainControllerOutput again = null!;
        for (var tick = 14; tick <= 25; tick++)
        {
            again = Step(c, Input(Model(speed: 10.0, commanded: 15.0), braking, tick: tick));
        }

        Assert.AreEqual(ControllerFailure.Brake, again.Display.Failures);
    }

    [TestMethod]
    public void BrakeFailure_EbResetDoesNotClearIt()
    {
        var c = NewController();
        var braking = new DriverInput { ServiceBrakeRequested = true };
        for (var tick = 1; tick <= 11; tick++)
        {
            Step(c, Input(Model(speed: 10.0, commanded: 15.0), braking, tick: tick));
        }

        var ebReset = Step(c, Input(Model(speed: 0.0, commanded: 15.0), new DriverInput { EmergencyBrakeResetRequested = true }, tick: 12));
        Assert.AreEqual(ControllerFailure.Brake, ebReset.Display.Failures);
        Assert.IsTrue(ebReset.Commands.EmergencyBrakeCommand);
    }

    // ---------------------------------------------------------------- engine failure

    [TestMethod]
    public void EngineFailure_SmallSpeedErrorOrLowPower_DoesNotTrigger()
    {
        var small = NewController();
        var low = NewController();
        for (var tick = 1; tick <= 30; tick++)
        {
            // 0.3 m/s below the setpoint: a small correction.
            var a = Step(small, Input(Model(speed: 9.7, commanded: 10.0), Automatic(), tick: tick));
            Assert.AreEqual(ControllerFailure.None, a.Display.Failures, $"small error, tick {tick}");

            // Large error but only ~5 kW (< 48 kW) with low gains.
            var b = Step(low, Input(Model(speed: 5.0, commanded: 10.0), Automatic(), tick: tick, engineer: EngineerSettings.Create(1_000.0, 0.0)));
            Assert.AreEqual(ControllerFailure.None, b.Display.Failures, $"low power, tick {tick}");
        }
    }

    [TestMethod]
    public void EngineFailure_BrakingDoesNotTrigger()
    {
        var c = NewController();
        var speed = 10.0;
        for (var tick = 1; tick <= 30; tick++)
        {
            var output = Step(c, Input(Model(speed: speed, commanded: 15.0), new DriverInput { ServiceBrakeRequested = true }, tick: tick));
            Assert.IsFalse(output.Display.Failures.HasFlag(ControllerFailure.Engine), $"tick {tick}");
            speed = Math.Max(0.0, speed - 1.2 * Dt);
        }
    }

    [TestMethod]
    public void EngineFailure_PowerWithoutAcceleration_TriggersServiceBrakeAndZeroPower()
    {
        var c = NewController();
        TrainControllerOutput output = null!;

        // 10 m/s below the setpoint, 20 000 W per (m/s) → 200 kW; the speed never rises.
        for (var tick = 1; tick <= 10; tick++)
        {
            output = Step(c, Input(Model(speed: 5.0, commanded: 15.0), Automatic(), tick: tick));
            NumericAssert.GreaterThan(output.Commands.PowerCommandWatts, 48_000.0, $"tick {tick}");
        }

        Assert.AreEqual(ControllerFailure.None, output.Display.Failures, "0.9 s: not yet.");

        output = Step(c, Input(Model(speed: 5.0, commanded: 15.0), Automatic(), tick: 11));
        Assert.AreEqual(ControllerFailure.Engine, output.Display.Failures);
        Assert.AreEqual(0.0, output.Commands.PowerCommandWatts);
        Assert.IsTrue(output.Commands.ServiceBrakeCommand, "Engine failure → service brake.");
        Assert.IsFalse(output.Commands.EmergencyBrakeCommand);
        Assert.AreEqual(TargetSpeedConstraint.Failure, output.Display.TargetLimitedBy);
    }

    [TestMethod]
    public void EngineFailure_ResetRejectedWhileMoving_AcceptedWhenStopped()
    {
        var c = NewController();
        for (var tick = 1; tick <= 11; tick++)
        {
            Step(c, Input(Model(speed: 5.0, commanded: 15.0), Automatic(), tick: tick));
        }

        // Service brake works: the train slows down (no brake failure).
        var moving = Step(c, Input(Model(speed: 4.88, commanded: 15.0), ResetFailures(), tick: 12));
        Assert.IsTrue(moving.Display.FailureResetRejected);
        Assert.AreEqual(ControllerFailure.Engine, moving.Display.Failures);

        var stopped = Step(c, Input(Model(speed: 0.0, commanded: 15.0), ResetFailures(), tick: 13));
        Assert.AreEqual(ControllerFailure.None, stopped.Display.Failures);
        NumericAssert.Positive(stopped.Commands.PowerCommandWatts, "Traction allowed again after the reset.");
    }

    // ---------------------------------------------------------------- interaction

    [TestMethod]
    public void EngineFailure_ThenIneffectiveServiceBrake_BrakeFailure_EmergencyBrake()
    {
        var c = NewController();
        TrainControllerOutput output = null!;

        // Neither traction nor service brake changes the speed.
        for (var tick = 1; tick <= 11; tick++)
        {
            output = Step(c, Input(Model(speed: 5.0, commanded: 15.0), Automatic(), tick: tick));
        }

        Assert.AreEqual(ControllerFailure.Engine, output.Display.Failures);
        Assert.IsTrue(output.Commands.ServiceBrakeCommand);
        Assert.IsFalse(output.Commands.EmergencyBrakeCommand);

        // Service brake commanded from tick 11 → window ticks 12..21.
        for (var tick = 12; tick <= 21; tick++)
        {
            output = Step(c, Input(Model(speed: 5.0, commanded: 15.0), Automatic(), tick: tick));
        }

        Assert.AreEqual(ControllerFailure.Engine | ControllerFailure.Brake, output.Display.Failures);
        Assert.IsTrue(output.Commands.EmergencyBrakeCommand, "Ineffective service brake → emergency brake.");
    }

    [TestMethod]
    public void FailureThresholds_AreConfigurable()
    {
        var policy = ControllerPolicy.Default with { BrakeFailureDetectionSeconds = 0.5 };
        var c = NewController();
        TrainControllerOutput output = null!;
        for (var tick = 1; tick <= 6; tick++)
        {
            output = Step(c, Input(Model(speed: 10.0, commanded: 15.0), new DriverInput { ServiceBrakeRequested = true }, tick: tick, policy: policy));
        }

        Assert.AreEqual(ControllerFailure.Brake, output.Display.Failures, "0.5 s window configured.");
        Assert.AreEqual(0.5, ControllerPolicy.Default.SignalPickupFailureLatchSeconds);
        Assert.AreEqual(1.0, ControllerPolicy.Default.BrakeFailureMinimumSpeedMetersPerSecond);
        Assert.AreEqual(1.0, ControllerPolicy.Default.BrakeFailureDetectionSeconds);
        Assert.AreEqual(0.3, ControllerPolicy.Default.BrakeFailureMinimumDecelerationMetersPerSecondSquared);
        Assert.AreEqual(0.5, ControllerPolicy.Default.EngineFailureMinimumSpeedErrorMetersPerSecond);
        Assert.AreEqual(0.10, ControllerPolicy.Default.EngineFailureMinimumPowerFraction);
        Assert.AreEqual(1.0, ControllerPolicy.Default.EngineFailureDetectionSeconds);
        Assert.AreEqual(0.0, ControllerPolicy.Default.EngineFailureMaximumAccelerationMetersPerSecondSquared);
    }
}
