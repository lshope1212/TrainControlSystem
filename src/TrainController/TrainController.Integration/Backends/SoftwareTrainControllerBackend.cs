using TrainController.Abstractions.Fleet;
using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;
using TrainController.Core.Services;

namespace TrainController.Integration.Backends;

/// <summary>
/// Software backend: an adapter that owns one independent <see cref="SoftwareTrainController"/>
/// (TrainController.Core) per Software train and forwards each evaluation to it.
/// No control logic here, no TCP, no Raspberry Pi.
/// </summary>
public sealed class SoftwareTrainControllerBackend : ITrainControllerBackend
{
    private readonly IReadOnlyDictionary<string, SoftwareTrainController> _controllers;

    public SoftwareTrainControllerBackend()
    {
        _controllers = TrainFleet.SoftwareTrainIds.ToDictionary(
            id => id,
            id => new SoftwareTrainController(id),
            StringComparer.Ordinal);
    }

    public ControllerType ControllerType => ControllerType.Software;

    /// <exception cref="InvalidOperationException">The train is not a Software train.</exception>
    public SoftwareTrainController GetController(string trainId) =>
        _controllers.TryGetValue(trainId, out var controller)
            ? controller
            : throw new InvalidOperationException($"'{trainId}' is not a Software-controlled train.");

    public Task<TrainControllerOutput> EvaluateAsync(TrainControllerInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        var controller = GetController(input.TrainId);

        lock (controller)
        {
            return Task.FromResult(controller.Step(input));
        }
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        foreach (var controller in _controllers.Values)
        {
            lock (controller)
            {
                controller.Reset();
            }
        }

        return Task.CompletedTask;
    }
}
