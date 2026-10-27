using TrainModel.Core.Models;

namespace TrainModel.Core.Interfaces;

/// <summary>
/// The Train Model plant. Accepts the current inputs, advances the physics by a
/// fixed timestep, and exposes an immutable state snapshot. All values metric.
///
/// Usage per tick:
///   1. ApplyInputs(latest inputs)          // from controller, track, users
///   2. Step(dt)                            // integrate physics once
///   3. read CurrentState / BuildOutputs    // publish results
/// </summary>
public interface ITrainSimulationService
{
    /// <summary>The train being simulated (specs + live kinematic/onboard state).</summary>
    Train Train { get; }

    /// <summary>Replace the current input vector (does not advance time).</summary>
    void ApplyInputs(TrainInputs inputs);

    /// <summary>Advance the simulation by one fixed timestep of <paramref name="delta"/>.</summary>
    void Step(TimeSpan delta);

    /// <summary>Immutable snapshot of the full observable state after the last Step.</summary>
    TrainState CurrentState { get; }

    /// <summary>
    /// Perform a station passenger exchange: board from platform demand (capped by
    /// capacity) and alight some onboard passengers. The Train Model decides who
    /// alights; boarding is limited by the Track Model's reported demand.
    /// Returns (boarded, alighted).
    /// </summary>
    (int boarded, int alighted) ExchangePassengers(int waitingToBoard, int alighting);

    /// <summary>Clear the emergency brake. Only the Train Controller may call this —
    /// a passenger request can set it but never reset it.</summary>
    void ClearEmergencyBrakeFromController();
}
