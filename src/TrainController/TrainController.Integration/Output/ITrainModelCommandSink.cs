using TrainControl.Contracts.Messages;

namespace TrainController.Integration.Output;

/// <summary>
/// Delivers Train-Model-facing commands to the real Train Model. Integration point for the
/// future named-pipe adapter; only used in Normal Mode.
/// </summary>
public interface ITrainModelCommandSink
{
    Task SendAsync(TrainControllerCommandMessage message, CancellationToken cancellationToken);
}

/// <summary>Used until the Train Model link exists: commands are dropped.</summary>
public sealed class NullTrainModelCommandSink : ITrainModelCommandSink
{
    public static NullTrainModelCommandSink Instance { get; } = new NullTrainModelCommandSink();

    public Task SendAsync(TrainControllerCommandMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
}
