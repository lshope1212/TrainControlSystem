namespace TrainControl.Common.Timing;

/// <summary>
/// Immutable snapshot of the shared simulation clock. The clock host maps it onto the
/// shared <c>SystemTimeMessage</c> contract for distribution.
/// </summary>
/// <param name="SystemTime">Current simulation time of day.</param>
/// <param name="SpeedMultiplier">Playback rate (1 = real time).</param>
/// <param name="IsPaused">True while simulation time is not advancing.</param>
/// <param name="Sequence">Increases whenever the clock's state changes.</param>
public readonly record struct SimulationClockState(TimeSpan SystemTime, double SpeedMultiplier, bool IsPaused, long Sequence);
