using TrainControl.Contracts.Messages;

namespace CTC.Wpf.Services;

/// <summary>
/// Sends CTC's clock-control requests to the shared simulation clock. CTC is the only module
/// given this capability; every other module only receives the clock's state.
/// </summary>
public interface ISimulationClockControlSender
{
    /// <summary>
    /// Completes when the request reached the clock. That is NOT confirmation that the clock
    /// changed: the authoritative result arrives later as a <see cref="SystemTimeMessage"/>.
    /// </summary>
    /// <exception cref="CTC.Core.Exceptions.MessageSendException">The clock could not be reached.</exception>
    Task SendAsync(SimulationTimeControlMessage request, CancellationToken cancellationToken = default);
}
