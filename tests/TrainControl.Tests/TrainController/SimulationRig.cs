using System.Diagnostics;
using TrainController.Abstractions.Fleet;
using TrainController.Integration.Configuration;
using TrainController.Integration.Execution;
using TrainController.Integration.Logging;
using TrainController.Integration.ModelInput;
using TrainController.Integration.Output;
using TrainController.Integration.Routing;
using TrainController.Integration.Simulation;
using TrainController.Integration.State;

namespace TrainControl.Tests.TrainController;

/// <summary>Subsystem wired with fake backends and a recording Train Model sink.</summary>
internal sealed class SimulationRig
{
    public SimulationRig(Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        Options = TrainControllerRuntimeOptions.Default;
        Log = new InMemoryTrainControllerEventLog();
        Registry = new TrainStateRegistry(Options.StartupDefaults);
        Software = new FakeTrainControllerBackend(ControllerType.Software);
        Hardware = new FakeTrainControllerBackend(ControllerType.Hardware);
        var router = new TrainControllerRouter(Software, Hardware);
        Normal = new NormalModelInputProvider();
        TestMode = new TestModeController(Normal, new TestModelInputProvider(Registry), Log);
        Sink = new RecordingTrainModelSink();
        Engine = new TrainControllerTickEngine(
            Registry, TestMode, new TrainControllerExecutionService(router, Log), new OutputRouter(Registry, Sink, Log), router, Options, Log);
        Simulation = new TestSimulationController(Engine, TestMode, Options, Log, delay);
    }

    public TrainControllerRuntimeOptions Options { get; }

    public InMemoryTrainControllerEventLog Log { get; }

    public TrainStateRegistry Registry { get; }

    public FakeTrainControllerBackend Software { get; }

    public FakeTrainControllerBackend Hardware { get; }

    public NormalModelInputProvider Normal { get; }

    public TestModeController TestMode { get; }

    public RecordingTrainModelSink Sink { get; }

    public TrainControllerTickEngine Engine { get; }

    public TestSimulationController Simulation { get; }

    public void ActivateTestTrain(string trainId, double speed = 0.0)
    {
        var model = Registry.Get(trainId).TestModel;
        model.IsActive = true;
        model.ActualSpeedMetersPerSecond = speed;
        model.AuthorizedSpeedMetersPerSecond = 10.0;
        model.RemainingAuthorityMeters = 10_000.0;
    }

    public IEnumerable<string> EvaluatedTrainIds() =>
        Software.Snapshot().Concat(Hardware.Snapshot()).Select(i => i.TrainId);

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 10_000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > timeoutMilliseconds)
            {
                Assert.Fail("Timed out waiting for condition.");
            }

            await Task.Delay(5);
        }
    }
}
