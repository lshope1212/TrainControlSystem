namespace TrainControl.Common.Timing;

/// <summary>
/// Control surface of the shared simulation clock. In the integrated system only the clock
/// host calls it, and only on behalf of CTC's clock-control requests; other modules get
/// <see cref="ISimulationClockSource"/> (or the distributed clock state) instead.
/// </summary>
public interface ISimulationClockController
{
    /// <summary>Runs (or resumes) the clock. Has no effect at the end of the simulated day.</summary>
    void Run();

    void Pause();

    /// <exception cref="ArgumentOutOfRangeException">Outside the supported range (1..10).</exception>
    void SetSpeed(double speedMultiplier);

    /// <exception cref="ArgumentOutOfRangeException">Not a time within the simulated day.</exception>
    void SetTime(TimeSpan systemTime);

    /// <summary>Returns to the startup state: start time, paused, 1x.</summary>
    void Reset();
}
