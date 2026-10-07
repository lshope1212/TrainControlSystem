using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;
using TrainController.Integration.Logging;
using TrainController.Integration.Mapping;
using TrainController.Integration.State;

namespace TrainController.Integration.Output;

/// <summary>
/// Single output-routing point.
/// Always: the result is recorded on the train's slot (feeds the Main UI, and the Test UI
/// output panel in Test Mode).
/// Normal Mode only: Train-Model-facing commands go to the real Train Model sink.
/// In Test Mode nothing is sent to the real Train Model.
/// </summary>
public sealed class OutputRouter
{
    private readonly TrainStateRegistry _registry;
    private readonly ITrainModelCommandSink _trainModel;
    private readonly ITrainControllerEventLog _log;

    public OutputRouter(TrainStateRegistry registry, ITrainModelCommandSink trainModelSink, ITrainControllerEventLog? log = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _trainModel = trainModelSink ?? throw new ArgumentNullException(nameof(trainModelSink));
        _log = log ?? NullTrainControllerEventLog.Instance;
    }

    public async Task RouteAsync(TrainControllerInput input, TrainControllerOutput output, bool testMode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        _registry.Get(output.TrainId).RecordTickResult(input.Model, output);

        if (testMode)
        {
            return;
        }

        try
        {
            await _trainModel.SendAsync(TrainModelContractMapper.ToCommandMessage(output), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Log(TrainControllerLogLevel.Error, "Output", output.TrainId, $"Failed to deliver commands to the Train Model: {ex.Message}");
        }
    }
}
