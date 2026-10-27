using TrainController.Abstractions.Inputs;
using TrainController.Integration.State;

namespace TrainController.Integration.ModelInput;

/// <summary>Test Mode: each train's model input comes from its own <see cref="TestModelState"/>.</summary>
public sealed class TestModelInputProvider : IModelInputProvider
{
    private readonly TrainStateRegistry _registry;

    public TestModelInputProvider(TrainStateRegistry registry) =>
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public TrainModelInput? Peek(string trainId) => _registry.Get(trainId).TestModel.PeekModelInput();

    public TrainModelInput? TakeForTick(string trainId) => _registry.Get(trainId).TestModel.TakeModelInputForTick();
}
