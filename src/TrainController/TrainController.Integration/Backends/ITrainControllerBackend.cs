using TrainController.Abstractions.Fleet;
using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;

namespace TrainController.Integration.Backends;

/// <summary>
/// One Train Controller implementation as seen by the routing/execution layer.
/// Implemented by the Software backend (adapter over TrainController.Core) and the
/// Hardware backend (TCP client to the Raspberry Pi). Backends hold per-train runtime
/// state; they never share it between trains.
/// </summary>
public interface ITrainControllerBackend
{
    ControllerType ControllerType { get; }

    /// <summary>
    /// Executes exactly ONE controller step for <paramref name="input"/>.TrainId and returns
    /// the output for that same tick (synchronous request/response semantics, asynchronous I/O).
    /// </summary>
    Task<TrainControllerOutput> EvaluateAsync(TrainControllerInput input, CancellationToken cancellationToken);

    /// <summary>
    /// Clears controller RUNTIME state (PI integral, latches, station tracking, previous
    /// outputs, transient faults) for every train this backend serves. Engineer settings
    /// and vehicle data are not runtime state and are unaffected.
    /// </summary>
    Task ResetAsync(CancellationToken cancellationToken);
}
