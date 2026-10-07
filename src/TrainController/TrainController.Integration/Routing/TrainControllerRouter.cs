using TrainController.Abstractions.Fleet;
using TrainController.Integration.Backends;

namespace TrainController.Integration.Routing;

/// <summary>
/// Thin, fixed TrainId -> backend resolution. The assignment itself comes only from
/// <see cref="TrainFleet"/>; this class just maps a <see cref="ControllerType"/> onto the
/// backend instance. Not configurable, no switching, no fallback.
/// </summary>
public sealed class TrainControllerRouter
{
    private readonly ITrainControllerBackend _software;
    private readonly ITrainControllerBackend _hardware;

    public TrainControllerRouter(ITrainControllerBackend softwareBackend, ITrainControllerBackend hardwareBackend)
    {
        _software = softwareBackend ?? throw new ArgumentNullException(nameof(softwareBackend));
        _hardware = hardwareBackend ?? throw new ArgumentNullException(nameof(hardwareBackend));

        if (_software.ControllerType != ControllerType.Software)
        {
            throw new ArgumentException("Software backend must report ControllerType.Software.", nameof(softwareBackend));
        }

        if (_hardware.ControllerType != ControllerType.Hardware)
        {
            throw new ArgumentException("Hardware backend must report ControllerType.Hardware.", nameof(hardwareBackend));
        }
    }

    public IReadOnlyList<ITrainControllerBackend> Backends => new[] { _software, _hardware };

    /// <exception cref="UnknownTrainException"><paramref name="trainId"/> is not a fleet train.</exception>
    public ITrainControllerBackend Resolve(string? trainId) =>
        TrainFleet.GetControllerType(trainId) switch
        {
            ControllerType.Software => _software,
            ControllerType.Hardware => _hardware,
            var other => throw new InvalidOperationException($"No backend for controller type {other}."),
        };
}
