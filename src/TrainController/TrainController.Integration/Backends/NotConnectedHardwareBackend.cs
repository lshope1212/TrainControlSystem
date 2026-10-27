using TrainController.Abstractions.Fleet;
using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;
using TrainController.Integration.Hardware;

namespace TrainController.Integration.Backends;

/// <summary>
/// Stand-in Hardware backend used until the Raspberry Pi TCP backend exists (later phase).
/// Every evaluation fails with <see cref="ControllerCommunicationException"/>, so Hardware
/// trains run in fail-safe (power 0, emergency brake) — never on the Software controller.
/// </summary>
public sealed class NotConnectedHardwareBackend : ITrainControllerBackend, IHardwareConnectionStatus
{
    public HardwareConnectionState ConnectionState => HardwareConnectionState.Disconnected;

    public string ConnectionDetail => "Raspberry Pi backend not configured.";

    public ControllerType ControllerType => ControllerType.Hardware;

    public Task<TrainControllerOutput> EvaluateAsync(TrainControllerInput input, CancellationToken cancellationToken) =>
        Task.FromException<TrainControllerOutput>(
            new ControllerCommunicationException("Hardware controller not connected (Raspberry Pi backend not configured)."));

    public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
