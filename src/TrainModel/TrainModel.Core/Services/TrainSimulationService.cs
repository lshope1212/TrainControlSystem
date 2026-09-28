using TrainModel.Core.Interfaces;
using TrainModel.Core.Models;

namespace TrainModel.Core.Services;

/// <summary>
/// Stub implementation of <see cref="ITrainSimulationService"/>.
/// Deliberately does no real work yet.
/// </summary>
public class TrainSimulationService : ITrainSimulationService
{
    public Train Train { get; } = new Train { Id = "TRAIN-001" };

    public void Step(TimeSpan delta)
    {
        // Placeholder: real simulation stepping will live here.
    }
}
