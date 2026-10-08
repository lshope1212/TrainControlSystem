using TrainController.Integration.Backends;
using TrainController.Integration.Configuration;
using TrainController.Integration.Execution;
using TrainController.Integration.Hardware;
using TrainController.Integration.Logging;
using TrainController.Integration.ModelInput;
using TrainController.Integration.Output;
using TrainController.Integration.Routing;
using TrainController.Integration.Simulation;
using TrainController.Integration.State;

namespace TrainController.Integration;

/// <summary>
/// Explicit composition of the whole Train Controller subsystem. Built ONCE per process (the
/// WPF App creates it and hands the same instance to the Main UI and the Test UI view models).
/// No service locator, no static state.
/// </summary>
public sealed class TrainControllerSubsystem
{
    private TrainControllerSubsystem(
        TrainControllerRuntimeOptions options,
        ITrainControllerEventLog log,
        ITrainControllerBackend softwareBackend,
        ITrainControllerBackend hardwareBackend,
        ITrainModelCommandSink trainModelSink)
    {
        Options = options;
        Log = log;
        Registry = new TrainStateRegistry(options.StartupDefaults);
        SoftwareBackend = softwareBackend;
        HardwareBackend = hardwareBackend;
        Router = new TrainControllerRouter(softwareBackend, hardwareBackend);
        Execution = new TrainControllerExecutionService(Router, log);
        NormalModelInput = new NormalModelInputProvider();
        TestModelInput = new TestModelInputProvider(Registry);
        TestMode = new TestModeController(NormalModelInput, TestModelInput, log);
        OutputRouter = new OutputRouter(Registry, trainModelSink, log);
        Engine = new TrainControllerTickEngine(Registry, TestMode, Execution, OutputRouter, Router, options, log);
        Simulation = new TestSimulationController(Engine, TestMode, options, log);
    }

    public TrainControllerRuntimeOptions Options { get; }

    public ITrainControllerEventLog Log { get; }

    public TrainStateRegistry Registry { get; }

    public ITrainControllerBackend SoftwareBackend { get; }

    public ITrainControllerBackend HardwareBackend { get; }

    /// <summary>Raspberry Pi link status for the Main UI; null if the Hardware backend does not report one.</summary>
    public IHardwareConnectionStatus? HardwareConnection => HardwareBackend as IHardwareConnectionStatus;

    public TrainControllerRouter Router { get; }

    public TrainControllerExecutionService Execution { get; }

    /// <summary>Normal Mode model input; the future Train Model adapter submits status here.</summary>
    public NormalModelInputProvider NormalModelInput { get; }

    public TestModelInputProvider TestModelInput { get; }

    public TestModeController TestMode { get; }

    public OutputRouter OutputRouter { get; }

    public TrainControllerTickEngine Engine { get; }

    public TestSimulationController Simulation { get; }

    /// <param name="hardwareBackend">
    /// Raspberry Pi backend. Defaults to <see cref="NotConnectedHardwareBackend"/> until the
    /// TCP backend exists: Hardware trains then run fail-safe, never on the Software controller.
    /// </param>
    /// <param name="trainModelSink">Normal-Mode command destination; defaults to a no-op until the Train Model link exists.</param>
    public static TrainControllerSubsystem Create(
        TrainControllerRuntimeOptions? options = null,
        ITrainControllerBackend? hardwareBackend = null,
        ITrainModelCommandSink? trainModelSink = null,
        ITrainControllerEventLog? log = null)
    {
        var resolvedOptions = options ?? TrainControllerRuntimeOptions.Default;
        resolvedOptions.Validate();

        return new TrainControllerSubsystem(
            resolvedOptions,
            log ?? new InMemoryTrainControllerEventLog(),
            new SoftwareTrainControllerBackend(),
            hardwareBackend ?? new NotConnectedHardwareBackend(),
            trainModelSink ?? NullTrainModelCommandSink.Instance);
    }
}
