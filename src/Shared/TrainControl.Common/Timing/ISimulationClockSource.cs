namespace TrainControl.Common.Timing;

/// <summary>
/// Read-only view of the shared simulation clock: what every module that only CONSUMES time
/// may see. Changing the clock needs <see cref="ISimulationClockController"/>.
/// </summary>
public interface ISimulationClockSource
{
    SimulationClockState Current { get; }
}
