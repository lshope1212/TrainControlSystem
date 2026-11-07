namespace TrainModel.Core.Simulation;

/// <summary>
/// Converts the shared clock's authoritative system time into FIXED physics steps. The Train
/// Model owns no clock, speed or pause of its own: it simulates exactly the simulated time
/// that elapsed between consecutive system times.
///
///   - Dt is constant (0.1 s). Every physics Step advances by exactly Dt.
///   - A faster clock produces larger system-time deltas, which become MORE steps, never a
///     larger dt — keeping the trapezoidal integration and control tuning valid.
///     E.g. 12:00:00.0 -> 12:00:00.5 is five Step(0.1 s).
///   - A paused clock sends the same time again: zero elapsed, zero steps.
///   - Simulated time that does not fill a whole step is carried to the next update
///     (exact integer tick arithmetic, so no rounding error accumulates).
/// </summary>
/// <remarks>
/// Discontinuities never run physics:
/// <list type="bullet">
///   <item>The first time received only establishes the baseline (receiving 12:00:00 does not
///         simulate twelve hours).</item>
///   <item>Time moving backwards (a TestUI, or the clock being reset or set earlier) rebases on
///         the new time; nothing is reversed and no negative step is run.</item>
///   <item>A forward jump larger than <see cref="MaxCatchUp"/> (the clock being SET later) also
///         rebases: trains do not teleport through hours of physics in one update.</item>
/// </list>
/// Not thread-safe: the caller applies system times one at a time, in arrival order.
/// </remarks>
public sealed class SystemTimeStepper
{
    public static readonly TimeSpan DefaultDt = TimeSpan.FromSeconds(0.1);

    /// <summary>
    /// Largest delta simulated as continuous running. The shared clock sends about every
    /// 100 ms real time, so even at 10x a normal delta is about 1 s; this leaves room for
    /// delayed or skipped updates.
    /// </summary>
    public static readonly TimeSpan MaxCatchUp = TimeSpan.FromSeconds(10);

    private TimeSpan? _lastSystemTime;

    public SystemTimeStepper()
        : this(DefaultDt)
    {
    }

    public SystemTimeStepper(TimeSpan dt)
    {
        if (dt <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(dt), "dt must be positive.");
        }

        Dt = dt;
    }

    /// <summary>Fixed physics timestep.</summary>
    public TimeSpan Dt { get; }

    /// <summary>The last system time applied, or null before the first one.</summary>
    public TimeSpan? LastSystemTime => _lastSystemTime;

    /// <summary>Simulated time received but not yet simulated (always less than <see cref="Dt"/>).</summary>
    public TimeSpan Remainder { get; private set; }

    /// <summary>
    /// Applies the next authoritative system time and calls <paramref name="step"/> once per
    /// fixed step now due, always with <see cref="Dt"/>.
    /// </summary>
    public SystemTimeAdvance Advance(TimeSpan systemTime, Action<TimeSpan> step)
    {
        ArgumentNullException.ThrowIfNull(step);

        if (_lastSystemTime is not { } last)
        {
            return Rebase(systemTime, SystemTimeAdvanceKind.Baseline);
        }

        var elapsed = systemTime - last;
        if (elapsed < TimeSpan.Zero)
        {
            return Rebase(systemTime, SystemTimeAdvanceKind.MovedBackward);
        }

        if (elapsed > MaxCatchUp)
        {
            return Rebase(systemTime, SystemTimeAdvanceKind.JumpedForward);
        }

        _lastSystemTime = systemTime;

        long pending = Remainder.Ticks + elapsed.Ticks;
        int steps = (int)(pending / Dt.Ticks);
        Remainder = TimeSpan.FromTicks(pending % Dt.Ticks);

        for (int i = 0; i < steps; i++)
        {
            step(Dt);
        }

        return new SystemTimeAdvance(SystemTimeAdvanceKind.Advanced, steps);
    }

    /// <summary>Forgets the baseline, so the next system time only re-establishes it.</summary>
    public void Rebaseline()
    {
        _lastSystemTime = null;
        Remainder = TimeSpan.Zero;
    }

    private SystemTimeAdvance Rebase(TimeSpan systemTime, SystemTimeAdvanceKind kind)
    {
        _lastSystemTime = systemTime;
        Remainder = TimeSpan.Zero;
        return new SystemTimeAdvance(kind, 0);
    }
}

/// <summary>What applying one system time did.</summary>
public readonly record struct SystemTimeAdvance(SystemTimeAdvanceKind Kind, int Steps);

public enum SystemTimeAdvanceKind
{
    /// <summary>Normal progression; <see cref="SystemTimeAdvance.Steps"/> may be 0 (paused, or less than one step elapsed).</summary>
    Advanced,

    /// <summary>First time received: baseline only, no physics.</summary>
    Baseline,

    /// <summary>Time moved backwards: rebased, no physics.</summary>
    MovedBackward,

    /// <summary>Time jumped forward by more than <see cref="SystemTimeStepper.MaxCatchUp"/>: rebased, no physics.</summary>
    JumpedForward,
}
