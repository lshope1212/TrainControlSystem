namespace TrainControl.Common.Timing;

/// <summary>
/// The system's single authoritative simulation clock: owns simulation time of day, the
/// playback speed and the paused state. It has no timer of its own; its host feeds it
/// measured real time through <see cref="Tick"/>, and each tick advances simulated time by
/// <c>real elapsed x SpeedMultiplier</c>.
/// </summary>
/// <remarks>
/// <para>
/// Exactly one instance exists in the integrated system, hosted by the Launcher process
/// (modules are separate processes, so it is shared over named pipes, not as a DI singleton).
/// TestUIs may create their own instance purely to stand in for it during isolated testing.
/// </para>
/// <para>
/// Startup state: <see cref="DefaultStartTime"/> (12:00:00), 1x, PAUSED, so nothing moves
/// before the dispatcher has loaded the track and schedule.
/// </para>
/// <para>
/// One simulated day only: time never passes <see cref="EndOfDay"/> (23:59:59). Reaching it
/// pauses the clock rather than rolling over to 00:00:00, which would look to every module
/// like time moving backwards.
/// </para>
/// <para>
/// A single tick never counts more than <see cref="MaxRealTickInterval"/> of real time, so a
/// host stalled for a long time (e.g. at a debugger breakpoint) does not make the simulation
/// leap forward; short delays are still fully compensated.
/// </para>
/// <para>Not thread-safe: the host serializes ticks and control requests.</para>
/// </remarks>
public sealed class SimulationClockService : ISimulationClockSource, ISimulationClockController, ITickSource
{
    public const double MinSpeedMultiplier = 1.0;
    public const double MaxSpeedMultiplier = 10.0;

    public static readonly TimeSpan DefaultStartTime = new TimeSpan(12, 0, 0);
    public static readonly TimeSpan EndOfDay = new TimeSpan(23, 59, 59);
    public static readonly TimeSpan MaxRealTickInterval = TimeSpan.FromSeconds(1);

    private TimeSpan _systemTime;
    private double _speedMultiplier = MinSpeedMultiplier;
    private bool _isPaused = true;
    private long _sequence;

    public SimulationClockService()
        : this(DefaultStartTime)
    {
    }

    /// <exception cref="ArgumentOutOfRangeException">Not a time within the simulated day.</exception>
    public SimulationClockService(TimeSpan startTime)
    {
        ValidateTime(startTime, nameof(startTime));
        StartTime = startTime;
        _systemTime = startTime;
    }

    /// <summary>Time the clock starts at, and returns to on <see cref="Reset"/>.</summary>
    public TimeSpan StartTime { get; }

    public SimulationClockState Current => new SimulationClockState(_systemTime, _speedMultiplier, _isPaused, _sequence);

    public TimeSpan Elapsed { get; private set; }

    /// <summary>
    /// Advances simulation time by <paramref name="delta"/> real time x the speed multiplier.
    /// Does nothing while paused or for a non-positive delta.
    /// </summary>
    public void Tick(TimeSpan delta)
    {
        if (_isPaused || delta <= TimeSpan.Zero)
        {
            return;
        }

        if (delta > MaxRealTickInterval)
        {
            delta = MaxRealTickInterval;
        }

        var simulated = TimeSpan.FromTicks((long)Math.Round(delta.Ticks * _speedMultiplier));
        var next = _systemTime + simulated;
        if (next >= EndOfDay)
        {
            simulated = EndOfDay - _systemTime;
            next = EndOfDay;
            _isPaused = true;
        }

        Elapsed += simulated;
        _systemTime = next;
        _sequence++;
    }

    public void Run()
    {
        if (!_isPaused || _systemTime >= EndOfDay)
        {
            return;
        }

        _isPaused = false;
        _sequence++;
    }

    public void Pause()
    {
        if (_isPaused)
        {
            return;
        }

        _isPaused = true;
        _sequence++;
    }

    public void SetSpeed(double speedMultiplier)
    {
        // NaN fails both comparisons, so it is rejected too.
        if (!(speedMultiplier >= MinSpeedMultiplier && speedMultiplier <= MaxSpeedMultiplier))
        {
            throw new ArgumentOutOfRangeException(nameof(speedMultiplier), speedMultiplier,
                $"Simulation speed must be between {MinSpeedMultiplier:0}x and {MaxSpeedMultiplier:0}x.");
        }

        _speedMultiplier = speedMultiplier;
        _sequence++;
    }

    /// <remarks>Keeps the paused/running state and speed. Moving time backwards is allowed; modules handle it.</remarks>
    public void SetTime(TimeSpan systemTime)
    {
        ValidateTime(systemTime, nameof(systemTime));

        _systemTime = systemTime;
        if (systemTime >= EndOfDay)
        {
            _isPaused = true;
        }

        _sequence++;
    }

    public void Reset()
    {
        _systemTime = StartTime;
        _speedMultiplier = MinSpeedMultiplier;
        _isPaused = true;
        Elapsed = TimeSpan.Zero;
        _sequence++;
    }

    private static void ValidateTime(TimeSpan time, string parameterName)
    {
        if (time < TimeSpan.Zero || time > EndOfDay)
        {
            throw new ArgumentOutOfRangeException(parameterName, time, "Simulation time must be between 00:00:00 and 23:59:59.");
        }
    }
}
