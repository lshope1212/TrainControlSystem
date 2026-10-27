using TrainControl.Contracts.Messages;
using TrainController.Abstractions.Fleet;
using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;
using TrainController.Integration.Backends;
using TrainController.Integration.Output;

namespace TrainControl.Tests.TrainController;

/// <summary>Records calls; by default echoes identity with a neutral output. Contains no control logic.</summary>
internal sealed class FakeTrainControllerBackend : ITrainControllerBackend
{
    private readonly object _gate = new object();

    public FakeTrainControllerBackend(ControllerType controllerType, Func<TrainControllerInput, TrainControllerOutput>? behavior = null)
    {
        ControllerType = controllerType;
        Behavior = behavior ?? (input => new TrainControllerOutput { TrainId = input.TrainId, TickId = input.TickId });
    }

    public ControllerType ControllerType { get; }

    public Func<TrainControllerInput, TrainControllerOutput> Behavior { get; set; }

    public List<TrainControllerInput> Evaluated { get; } = new List<TrainControllerInput>();

    public int ResetCount { get; private set; }

    /// <summary>Thread-safe copy of <see cref="Evaluated"/> (the simulation loop may still be adding).</summary>
    public TrainControllerInput[] Snapshot()
    {
        lock (_gate)
        {
            return Evaluated.ToArray();
        }
    }

    public Task<TrainControllerOutput> EvaluateAsync(TrainControllerInput input, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Evaluated.Add(input);
        }

        return Task.FromResult(Behavior(input));
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        ResetCount++;
        return Task.CompletedTask;
    }
}

/// <summary>Records commands sent to the "real" Train Model.</summary>
internal sealed class RecordingTrainModelSink : ITrainModelCommandSink
{
    public List<TrainControllerCommandMessage> Sent { get; } = new List<TrainControllerCommandMessage>();

    public Task SendAsync(TrainControllerCommandMessage message, CancellationToken cancellationToken)
    {
        lock (Sent)
        {
            Sent.Add(message);
        }

        return Task.CompletedTask;
    }
}
