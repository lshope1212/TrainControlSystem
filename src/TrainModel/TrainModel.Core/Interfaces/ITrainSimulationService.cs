using TrainModel.Core.Models;

namespace TrainModel.Core.Interfaces;

/// <summary>
/// Placeholder contract for the train simulation. Real physics is not implemented yet.
/// </summary>
public interface ITrainSimulationService
{
    Train Train { get; }

    /// <summary>Advances the simulation by one time step.</summary>
    void Step(TimeSpan delta);
}
