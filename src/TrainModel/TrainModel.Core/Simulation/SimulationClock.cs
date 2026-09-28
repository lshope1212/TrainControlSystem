namespace TrainModel.Core.Simulation;

/// <summary>
/// Placeholder for simulation time management (wall clock vs. simulated time,
/// time multipliers, pause/resume). No timers are started yet.
/// </summary>
public class SimulationClock
{
    public TimeSpan Elapsed { get; private set; }

    public void Advance(TimeSpan delta) => Elapsed += delta;

    public void Reset() => Elapsed = TimeSpan.Zero;
}
