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

        var slot = _registry.Get(output.TrainId);
        LogSafetyEvents(output.TrainId, slot.LastOutput, output);
        slot.RecordTickResult(input.Model, output);

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

    /// <summary>Safety log: failure detected / cleared, rejected failure reset.</summary>
    private void LogSafetyEvents(string trainId, TrainControllerOutput? previous, TrainControllerOutput current)
    {
        var before = previous?.Display.Failures ?? ControllerFailure.None;
        var now = current.Display.Failures;

        foreach (var failure in new[] { ControllerFailure.SignalPickup, ControllerFailure.Brake, ControllerFailure.Engine, ControllerFailure.HardwareLink })
        {
            var was = (before & failure) != 0;
            var isNow = (now & failure) != 0;
            if (!was && isNow)
            {
                _log.Log(TrainControllerLogLevel.Error, "Safety", trainId, $"{FailureName(failure)} detected (latched; Reset failures required).");
            }
            else if (was && !isNow)
            {
                _log.Log(TrainControllerLogLevel.Info, "Safety", trainId, $"{FailureName(failure)} cleared by Reset failures.");
            }
        }

        if (current.Display.FailureResetRejected)
        {
            _log.Log(TrainControllerLogLevel.Warning, "Safety", trainId, $"Reset failures rejected: {current.Display.FailureResetRejectedReason}.");
        }
    }

    private static string FailureName(ControllerFailure failure) => failure switch
    {
        ControllerFailure.SignalPickup => "Signal pickup failure",
        ControllerFailure.Brake => "Brake failure",
        ControllerFailure.Engine => "Engine failure",
        ControllerFailure.HardwareLink => "Hardware link failure",
        _ => failure.ToString(),
    };
}
