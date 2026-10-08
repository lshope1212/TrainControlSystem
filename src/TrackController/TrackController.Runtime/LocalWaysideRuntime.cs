using TrackController.Contracts.Messages;
using TrackController.Core.Interfaces;
using TrainControl.Common.Communication;

namespace TrackController.Runtime;

/// <summary>Adapts the controller service to the same asynchronous boundary used by the Pi.</summary>
public sealed class LocalWaysideRuntime : IWaysideRuntime
{
    private readonly ITrackControllerService _controller;
    private readonly WaysideMessageRouter _receiver;

    public WaysideSnapshotMessage LatestSnapshot { get; private set; }

    /// <summary>Wraps an existing controller without creating a second copy of its state.</summary>
    public LocalWaysideRuntime(ITrackControllerService controller)
    {
        _controller = controller;
        _receiver = new(controller);
        LatestSnapshot = controller.GetSnapshot();
    }

    /// <summary>Handles an application message through the selected runtime and returns its result.</summary>
    public Task<object> ReceiveAsync(MessageEnvelope message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        object result = _receiver.Receive(message);
        LatestSnapshot = _controller.GetSnapshot();
        return Task.FromResult(result);
    }

    /// <summary>Refreshes runtime outputs so timeouts apply even when no new input arrives.</summary>
    public Task<WaysideSnapshotMessage> PollAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LatestSnapshot = _controller.GetSnapshot();
        return Task.FromResult(LatestSnapshot);
    }

    /// <summary>Pauses the controller before releasing its runtime wrapper.</summary>
    public ValueTask DisposeAsync()
    {
        _controller.SetSimulation(new()
        {
            IsPaused = true,
            SpeedMultiplier = LatestSnapshot.SpeedMultiplier
        });
        return ValueTask.CompletedTask;
    }
}
