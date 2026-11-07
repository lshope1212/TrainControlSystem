namespace TrainControl.Common.Timing;

/// <summary>
/// Something that advances simulated time when it is fed elapsed REAL (wall-clock) time.
/// Implemented by <see cref="SimulationClockService"/>: its host measures real time (e.g. with
/// a Stopwatch) and calls <see cref="Tick"/>; tests call it with fixed deltas so they never
/// depend on the wall clock.
/// </summary>
public interface ITickSource
{
    /// <summary>Total simulated time advanced by <see cref="Tick"/> (jumps from setting the time are not counted).</summary>
    TimeSpan Elapsed { get; }

    /// <summary>Advances simulated time for <paramref name="delta"/> of elapsed real time.</summary>
    void Tick(TimeSpan delta);
}
