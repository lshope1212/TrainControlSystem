using TrainModel.Core.Models;
using TrainModel.Core.Services;
using TrainModel.Core.Simulation;

namespace TrainControl.Tests.TrainModel;

/// <summary>
/// The Train Model follows the shared clock's system time with FIXED 0.1 s physics steps,
/// however fast the clock runs.
/// </summary>
[TestClass]
public class SystemTimeStepperTests
{
    private static readonly TimeSpan Noon = new TimeSpan(12, 0, 0);
    private static readonly TimeSpan Dt = TimeSpan.FromSeconds(0.1);

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    /// <summary>A stepper already synchronized to <see cref="Noon"/>, plus the steps it runs.</summary>
    private static (SystemTimeStepper Stepper, List<TimeSpan> Steps) Synchronized()
    {
        var stepper = new SystemTimeStepper();
        var steps = new List<TimeSpan>();
        stepper.Advance(Noon, steps.Add);
        return (stepper, steps);
    }

    [TestMethod]
    public void FirstTime_EstablishesBaseline_WithoutRunningPhysics()
    {
        var stepper = new SystemTimeStepper();
        var steps = new List<TimeSpan>();

        var advance = stepper.Advance(Noon, steps.Add);

        // Receiving 12:00:00 must not simulate twelve hours.
        Assert.AreEqual(SystemTimeAdvanceKind.Baseline, advance.Kind);
        Assert.AreEqual(0, advance.Steps);
        Assert.IsEmpty(steps);
        Assert.AreEqual(Noon, stepper.LastSystemTime);
    }

    [TestMethod]
    public void HalfASecond_RunsFiveFixedTenthSecondSteps()
    {
        var (stepper, steps) = Synchronized();

        var advance = stepper.Advance(Noon + Ms(500), steps.Add);

        Assert.AreEqual(SystemTimeAdvanceKind.Advanced, advance.Kind);
        Assert.AreEqual(5, advance.Steps);
        CollectionAssert.AreEqual(Enumerable.Repeat(Dt, 5).ToList(), steps);
    }

    [TestMethod]
    public void AcceleratedDelta_StillUsesFixedSteps_NeverALargerDt()
    {
        var (stepper, steps) = Synchronized();

        // 10x: one 100 ms update carries 1 s of simulated time -> 10 steps of 0.1 s.
        stepper.Advance(Noon + TimeSpan.FromSeconds(1), steps.Add);
        // A delayed update at 10x carrying 3.7 s -> 37 more steps of 0.1 s.
        stepper.Advance(Noon + TimeSpan.FromSeconds(4.7), steps.Add);

        Assert.HasCount(47, steps);
        Assert.IsTrue(steps.All(step => step == Dt));
    }

    [TestMethod]
    public void PausedClock_RepeatedTime_RunsNoPhysics()
    {
        var (stepper, steps) = Synchronized();
        stepper.Advance(Noon + Ms(300), steps.Add);
        steps.Clear();

        // A paused clock keeps sending the same time.
        for (int i = 0; i < 20; i++)
        {
            var advance = stepper.Advance(Noon + Ms(300), steps.Add);
            Assert.AreEqual(new SystemTimeAdvance(SystemTimeAdvanceKind.Advanced, 0), advance);
        }

        Assert.IsEmpty(steps);
    }

    [TestMethod]
    public void BackwardTime_Rebases_WithoutNegativeOrReversedSteps()
    {
        var (stepper, steps) = Synchronized();
        stepper.Advance(Noon + Ms(550), steps.Add);
        steps.Clear();

        var backward = stepper.Advance(Noon - TimeSpan.FromMinutes(5), steps.Add);

        Assert.AreEqual(SystemTimeAdvanceKind.MovedBackward, backward.Kind);
        Assert.IsEmpty(steps);
        Assert.AreEqual(TimeSpan.Zero, stepper.Remainder, "The old remainder must not leak into the new timeline.");

        // Physics continues normally from the new baseline.
        stepper.Advance(Noon - TimeSpan.FromMinutes(5) + Ms(200), steps.Add);
        CollectionAssert.AreEqual(new[] { Dt, Dt }, steps);
    }

    [TestMethod]
    public void HugeForwardJump_Rebases_InsteadOfSimulatingTheGap()
    {
        var (stepper, steps) = Synchronized();

        var jump = stepper.Advance(Noon + TimeSpan.FromHours(2), steps.Add);

        Assert.AreEqual(SystemTimeAdvanceKind.JumpedForward, jump.Kind);
        Assert.IsEmpty(steps);
        Assert.AreEqual(Noon + TimeSpan.FromHours(2), stepper.LastSystemTime);
    }

    [TestMethod]
    public void FractionalRemainder_IsCarriedAcrossUpdates_WithoutDrift()
    {
        var (stepper, steps) = Synchronized();

        // 0.25 s -> 2 steps, 0.05 s carried.
        stepper.Advance(Noon + Ms(250), steps.Add);
        Assert.HasCount(2, steps);
        Assert.AreEqual(Ms(50), stepper.Remainder);

        // +0.07 s -> 0.12 s pending -> 1 step, 0.02 s carried.
        stepper.Advance(Noon + Ms(320), steps.Add);
        Assert.HasCount(3, steps);
        Assert.AreEqual(Ms(20), stepper.Remainder);

        // 1000 uneven updates totalling 100 s: exactly 1000 steps in all, nothing lost or gained.
        var (longRun, longSteps) = Synchronized();
        var time = Noon;
        for (int i = 0; i < 1000; i++)
        {
            time += Ms(i % 2 == 0 ? 37 : 163);
            longRun.Advance(time, longSteps.Add);
        }

        Assert.HasCount(1000, longSteps);
        Assert.AreEqual(TimeSpan.Zero, longRun.Remainder);
    }

    [TestMethod]
    public void TrainPhysics_FollowingSystemTime_MatchesDirectFixedSteps()
    {
        // Two identical trains: one driven by system-time deltas, one stepped directly.
        static TrainSimulationService Plant()
        {
            var plant = new TrainSimulationService(new Train { Id = "T1", VelocityMetersPerSecond = 10.0 });
            plant.ApplyInputs(new TrainInputs { PowerCommandWatts = 60000, CommandedSpeedMetersPerSecond = 19.44, CommandedAuthorityMeters = 5000 });
            return plant;
        }

        var followed = Plant();
        var direct = Plant();
        var stepper = new SystemTimeStepper();
        stepper.Advance(Noon, followed.Step);

        // 5x for 2 s real time = 10 s simulated, sent every 100 ms (0.5 s each).
        for (int i = 1; i <= 20; i++)
        {
            stepper.Advance(Noon + Ms(500 * i), followed.Step);
        }

        for (int i = 0; i < 100; i++)
        {
            direct.Step(Dt);
        }

        Assert.AreEqual(direct.CurrentState.PositionMeters, followed.CurrentState.PositionMeters, 1e-9);
        Assert.AreEqual(direct.CurrentState.VelocityMetersPerSecond, followed.CurrentState.VelocityMetersPerSecond, 1e-9);
        Assert.IsGreaterThan(0.0, followed.CurrentState.PositionMeters);
    }
}
