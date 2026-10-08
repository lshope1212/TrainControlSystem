using TrainController.Abstractions.Inputs;

namespace TrainController.Integration.ModelInput;

/// <summary>
/// Source of the Train-Model-originating part of a controller input, per train.
/// Exactly one provider is active at a time (see <see cref="TestModeController"/>).
/// </summary>
public interface IModelInputProvider
{
    /// <summary>Current model input without consuming one-shot events; null if none is available.</summary>
    TrainModelInput? Peek(string trainId);

    /// <summary>Model input for one controller tick; consumes one-shot events (e.g. a beacon reception).</summary>
    TrainModelInput? TakeForTick(string trainId);
}
