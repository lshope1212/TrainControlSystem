namespace TrainModel.Core.Simulation;

/// <summary>
/// Simulation time manager. Separates the FIXED physics timestep from the
/// variable playback SPEED so a full day can be run faster or slower without
/// changing the physics:
///
///   - DtSeconds is constant (e.g. 0.1 s). Every physics Step advances by this.
///   - SpeedMultiplier (1..10) sets how many Steps are consumed per real second.
///   - Pause consumes zero Steps.
///
/// Running at 10x means the scheduler calls Step ~10x as often, NOT that it uses
/// a 10x-larger dt — keeping the trapezoidal integration and control tuning valid.
/// </summary>
public class SimulationClock
{
    private double _accumulatedRealSeconds;

    public SimulationClock(double dtSeconds = 0.1)
    {
        if (dtSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(dtSeconds), "dt must be positive.");
        }

        DtSeconds = dtSeconds;
    }

    /// <summary>Fixed physics timestep, seconds. Shared system-wide.</summary>
    public double DtSeconds { get; }

    /// <summary>Playback rate: 1 = real time, up to 10 = ten times faster.</summary>
    public double SpeedMultiplier { get; private set; } = 1.0;

    public bool IsPaused { get; private set; }

    /// <summary>Total SIMULATED time elapsed.</summary>
    public TimeSpan Elapsed { get; private set; }

    public void SetSpeed(double multiplier)
    {
        if (multiplier < 1.0 || multiplier > 10.0)
        {
            throw new ArgumentOutOfRangeException(nameof(multiplier), "Speed must be 1..10.");
        }

        SpeedMultiplier = multiplier;
    }

    public void Pause() => IsPaused = true;

    public void Resume() => IsPaused = false;

    public void Reset()
    {
        Elapsed = TimeSpan.Zero;
        _accumulatedRealSeconds = 0.0;
    }

    /// <summary>
    /// Given the real wall-clock time that has passed, return how many fixed-dt
    /// physics steps to run now, and advance simulated time by that amount.
    /// The scheduler calls this each frame/timer tick, then calls the simulation's
    /// Step() the returned number of times.
    /// </summary>
    public int StepsFor(double realSecondsElapsed)
    {
        if (IsPaused || realSecondsElapsed <= 0.0)
        {
            return 0;
        }

        _accumulatedRealSeconds += realSecondsElapsed * SpeedMultiplier;

        int steps = (int)(_accumulatedRealSeconds / DtSeconds);
        if (steps > 0)
        {
            _accumulatedRealSeconds -= steps * DtSeconds;
            Elapsed += TimeSpan.FromSeconds(steps * DtSeconds);
        }

        return steps;
    }
}
