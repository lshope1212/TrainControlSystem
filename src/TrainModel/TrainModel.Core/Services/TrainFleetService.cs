using System.Collections.Concurrent;
using TrainModel.Core.Interfaces;
using TrainModel.Core.Models;

namespace TrainModel.Core.Services;

/// <summary>
/// Default fleet: a thread-safe dictionary of per-train plants, stepped together.
/// The per-train physics live in <see cref="TrainSimulationService"/>; this layer
/// only owns the collection, routes inputs by id, and steps everyone each tick.
/// </summary>
public class TrainFleetService : ITrainFleetService
{
    private readonly ConcurrentDictionary<string, ITrainSimulationService> _trains = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ITrainSimulationService> Trains => _trains.Values.ToList();

    public ITrainSimulationService AddTrain(Train train)
    {
        ArgumentNullException.ThrowIfNull(train);
        var plant = new TrainSimulationService(train);
        _trains[train.Id] = plant;
        return plant;
    }

    public bool RemoveTrain(string trainId) => _trains.TryRemove(trainId, out _);

    public ITrainSimulationService? GetTrain(string trainId) =>
        _trains.TryGetValue(trainId, out var plant) ? plant : null;

    public void ApplyInputs(string trainId, TrainInputs inputs)
    {
        if (_trains.TryGetValue(trainId, out var plant))
        {
            plant.ApplyInputs(inputs);
        }
    }

    public void StepAll(TimeSpan delta)
    {
        foreach (var plant in _trains.Values)
        {
            plant.Step(delta);
        }
    }

    public IReadOnlyList<TrainState> SnapshotAll() =>
        _trains.Values.Select(p => p.CurrentState).ToList();
}
