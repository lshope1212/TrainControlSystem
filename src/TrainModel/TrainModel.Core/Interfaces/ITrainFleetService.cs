using TrainModel.Core.Models;

namespace TrainModel.Core.Interfaces;

/// <summary>
/// Owns every train currently on the railway and advances them together on the
/// shared simulation clock. Each train is an independent plant
/// (<see cref="ITrainSimulationService"/>) keyed by train id. Inbound messages are
/// routed to the matching train; the fleet is the unit the UI and pipe layer talk to.
/// </summary>
public interface ITrainFleetService
{
    /// <summary>All trains currently on the railway.</summary>
    IReadOnlyCollection<ITrainSimulationService> Trains { get; }

    /// <summary>Add (dispatch) a train onto the railway. Returns its plant.</summary>
    ITrainSimulationService AddTrain(Train train);

    /// <summary>Remove (return to yard) a train by id. Returns true if removed.</summary>
    bool RemoveTrain(string trainId);

    /// <summary>Get a train's plant by id, or null if not on the railway.</summary>
    ITrainSimulationService? GetTrain(string trainId);

    /// <summary>Apply inputs to one train (routed by id). No-op if the id is unknown.</summary>
    void ApplyInputs(string trainId, TrainInputs inputs);

    /// <summary>Advance EVERY train by one fixed timestep.</summary>
    void StepAll(TimeSpan delta);

    /// <summary>Snapshot of every train's current state (for the roster / outputs).</summary>
    IReadOnlyList<TrainState> SnapshotAll();
}
