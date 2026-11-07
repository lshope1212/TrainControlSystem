using TrainControl.Common.Timing;

namespace TrainControl.Tests.Common;

/// <summary>
/// The shared simulation clock. Real time is fed in through <see cref="SimulationClockService.Tick"/>
/// with fixed deltas, so no test depends on the wall clock.
/// </summary>
[TestClass]
public class SimulationClockServiceTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private static TimeSpan At(int hours, int minutes, int seconds) => new TimeSpan(hours, minutes, seconds);

    private static SimulationClockService RunningClock(double speed = 1.0)
    {
        var clock = new SimulationClockService();
        clock.SetSpeed(speed);
        clock.Run();
        return clock;
    }

    private static void TickTimes(SimulationClockService clock, int count)
    {
        for (int i = 0; i < count; i++)
        {
            clock.Tick(Interval);
        }
    }

    [TestMethod]
    public void StartsAtNoon_Paused_At1x()
    {
        var state = new SimulationClockService().Current;

        Assert.AreEqual(At(12, 0, 0), state.SystemTime);
        Assert.AreEqual(1.0, state.SpeedMultiplier);
        Assert.IsTrue(state.IsPaused);
    }

    [TestMethod]
    public void WhilePaused_TicksDoNotAdvanceTime()
    {
        var clock = new SimulationClockService();

        TickTimes(clock, 50);

        Assert.AreEqual(At(12, 0, 0), clock.Current.SystemTime);
        Assert.AreEqual(TimeSpan.Zero, clock.Elapsed);
    }

    [TestMethod]
    public void At1x_EachTickAdvancesByTheRealTimeElapsed()
    {
        var clock = RunningClock();

        clock.Tick(Interval);
        Assert.AreEqual(At(12, 0, 0) + TimeSpan.FromMilliseconds(100), clock.Current.SystemTime);

        TickTimes(clock, 9);
        Assert.AreEqual(At(12, 0, 1), clock.Current.SystemTime);
    }

    [TestMethod]
    public void UsesMeasuredRealTime_NotANominalInterval()
    {
        var clock = RunningClock();

        // A late timer callback (250 ms instead of 100 ms) is fully counted: no drift.
        clock.Tick(TimeSpan.FromMilliseconds(250));

        Assert.AreEqual(At(12, 0, 0) + TimeSpan.FromMilliseconds(250), clock.Current.SystemTime);
    }

    [TestMethod]
    [DataRow(2.0, 200)]
    [DataRow(5.0, 500)]
    [DataRow(10.0, 1000)]
    public void HigherMultipliers_AdvanceProportionallyFaster(double speed, int expectedMillisecondsPerTick)
    {
        var clock = RunningClock(speed);

        clock.Tick(Interval);

        Assert.AreEqual(At(12, 0, 0) + TimeSpan.FromMilliseconds(expectedMillisecondsPerTick), clock.Current.SystemTime);
        Assert.AreEqual(speed, clock.Current.SpeedMultiplier);
    }

    [TestMethod]
    public void Pause_StopsAdvancement_AndResumeContinuesFromThePausedTime()
    {
        var clock = RunningClock();
        TickTimes(clock, 10);

        clock.Pause();
        TickTimes(clock, 30);
        Assert.IsTrue(clock.Current.IsPaused);
        Assert.AreEqual(At(12, 0, 1), clock.Current.SystemTime);

        clock.Run();
        TickTimes(clock, 10);
        Assert.IsFalse(clock.Current.IsPaused);
        Assert.AreEqual(At(12, 0, 2), clock.Current.SystemTime);
    }

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(0.5)]
    [DataRow(10.5)]
    [DataRow(-1.0)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    public void InvalidSpeed_IsRejected_AndSpeedIsUnchanged(double speed)
    {
        var clock = RunningClock(5.0);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => clock.SetSpeed(speed));
        Assert.AreEqual(5.0, clock.Current.SpeedMultiplier);
    }

    [TestMethod]
    public void SetTime_UpdatesAuthoritativeTime_AndKeepsRunningStateAndSpeed()
    {
        var clock = RunningClock(2.0);

        clock.SetTime(At(8, 30, 0));

        Assert.AreEqual(At(8, 30, 0), clock.Current.SystemTime);
        Assert.IsFalse(clock.Current.IsPaused);
        Assert.AreEqual(2.0, clock.Current.SpeedMultiplier);

        clock.Tick(Interval);
        Assert.AreEqual(At(8, 30, 0) + TimeSpan.FromMilliseconds(200), clock.Current.SystemTime);
    }

    [TestMethod]
    public void SetTime_OutsideTheDay_IsRejected()
    {
        var clock = new SimulationClockService();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => clock.SetTime(TimeSpan.FromHours(24)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => clock.SetTime(TimeSpan.FromSeconds(-1)));
        Assert.AreEqual(At(12, 0, 0), clock.Current.SystemTime);
    }

    [TestMethod]
    public void Reset_ReturnsToStartTime_Paused_At1x()
    {
        var clock = RunningClock(10.0);
        TickTimes(clock, 25);
        clock.SetTime(At(15, 0, 0));

        clock.Reset();

        Assert.AreEqual(new SimulationClockState(At(12, 0, 0), 1.0, true, clock.Current.Sequence), clock.Current);
        Assert.AreEqual(TimeSpan.Zero, clock.Elapsed);
    }

    [TestMethod]
    public void EndOfDay_ClampsAndPauses_RatherThanRollingOver()
    {
        var clock = RunningClock(10.0);
        clock.SetTime(At(23, 59, 58));

        TickTimes(clock, 5);

        Assert.AreEqual(SimulationClockService.EndOfDay, clock.Current.SystemTime);
        Assert.IsTrue(clock.Current.IsPaused);

        // Cannot be run past the end of the day.
        clock.Run();
        Assert.IsTrue(clock.Current.IsPaused);
    }

    [TestMethod]
    public void LongHostStall_CountsAtMostOneSecondOfRealTime()
    {
        var clock = RunningClock();

        clock.Tick(TimeSpan.FromSeconds(30));

        Assert.AreEqual(At(12, 0, 1), clock.Current.SystemTime);
    }

    [TestMethod]
    public void Sequence_IncreasesOnEveryChange_AndNotWhilePaused()
    {
        var clock = new SimulationClockService();
        long start = clock.Current.Sequence;

        clock.Tick(Interval);
        Assert.AreEqual(start, clock.Current.Sequence, "A paused tick changes nothing.");

        clock.Run();
        long afterRun = clock.Current.Sequence;
        clock.Tick(Interval);
        clock.SetSpeed(5.0);

        Assert.IsGreaterThan(start, afterRun);
        Assert.AreEqual(afterRun + 2, clock.Current.Sequence);
    }
}
