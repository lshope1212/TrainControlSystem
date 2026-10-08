using TrainController.Abstractions.Inputs;
using TrainController.Integration.Configuration;
using TrainController.Integration.Execution;
using TrainController.Integration.Logging;
using TrainController.Integration.ModelInput;
using TrainController.Integration.Output;
using TrainController.Integration.Routing;
using TrainController.Integration.State;

namespace TrainController.Integration.Simulation;

/// <summary>Outcome of one tick.</summary>
public sealed record TickReport(long TickId, double SimulationTimeSeconds, IReadOnlyList<string> ExecutedTrainIds);

/// <summary>
/// Executes controller ticks for the whole subsystem. One tick = for every ACTIVE train,
/// assemble one <see cref="TrainControllerInput"/> (model input from the current provider +
/// that train's Driver input + Engineer settings + timing), execute it once, route the output.
/// </summary>
/// <remarks>
/// <para>
/// A train runs only if its model input reports <c>IsActive</c>. Which train a UI is
/// showing has no effect. Inactive trains are skipped without consuming their pending
/// driver presses or beacon receptions.
/// </para>
/// <para>
/// Simulation time is TickId × fixed timestep (no accumulation drift). A TickId is consumed
/// when a tick starts and is never reused, even if the tick is cancelled part-way.
/// Ticks and Reset are serialized.
/// </para>
/// </remarks>
public sealed class TrainControllerTickEngine
{
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly TrainStateRegistry _registry;
    private readonly TestModeController _testMode;
    private readonly TrainControllerExecutionService _execution;
    private readonly OutputRouter _output;
    private readonly TrainControllerRouter _router;
    private readonly TrainControllerRuntimeOptions _options;
    private readonly ITrainControllerEventLog _log;
    private long _tickCount;
    private double _simulationTimeSeconds;

    public TrainControllerTickEngine(
        TrainStateRegistry registry,
        TestModeController testMode,
        TrainControllerExecutionService execution,
        OutputRouter output,
        TrainControllerRouter router,
        TrainControllerRuntimeOptions options,
        ITrainControllerEventLog? log = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _testMode = testMode ?? throw new ArgumentNullException(nameof(testMode));
        _execution = execution ?? throw new ArgumentNullException(nameof(execution));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _log = log ?? NullTrainControllerEventLog.Instance;
    }

    /// <summary>Fixed simulated timestep used for every tick.</summary>
    public double DeltaTimeSeconds => _options.SimulationTimeStepSeconds;

    /// <summary>Number of ticks started since construction / last Reset (= last TickId).</summary>
    public long TickCount => Interlocked.Read(ref _tickCount);

    public double SimulationTimeSeconds => Volatile.Read(ref _simulationTimeSeconds);

    public async Task<TickReport> ExecuteTickAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (testMode, provider) = _testMode.Capture();
            var tickId = Interlocked.Increment(ref _tickCount);
            var simulationTime = tickId * _options.SimulationTimeStepSeconds;
            Volatile.Write(ref _simulationTimeSeconds, simulationTime);

            var executed = new List<string>();

            foreach (var slot in _registry.Trains)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var peek = provider.Peek(slot.TrainId);
                if (peek is null || !peek.IsActive)
                {
                    continue;
                }

                var model = provider.TakeForTick(slot.TrainId) ?? peek;
                if (!model.IsActive)
                {
                    continue;
                }

                var input = new TrainControllerInput
                {
                    TrainId = slot.TrainId,
                    TickId = tickId,
                    SimulationTimeSeconds = simulationTime,
                    DeltaTimeSeconds = _options.SimulationTimeStepSeconds,
                    Model = model,
                    Driver = slot.Driver.TakeForTick(),
                    Engineer = slot.Engineer,
                    Vehicle = _options.Vehicle,
                    Policy = _options.Policy,
                };

                var output = await _execution.ExecuteAsync(input, slot.LastOutput, cancellationToken);
                await _output.RouteAsync(input, output, testMode, cancellationToken);
                executed.Add(slot.TrainId);
            }

            return new TickReport(tickId, simulationTime, executed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Resets the runtime of ALL ten trains: tick counter, simulation time, every backend's
    /// controller runtime state, previous outputs and pending one-shot events. Engineer
    /// settings, vehicle data, Driver held settings and Test Model input values are kept.
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Interlocked.Exchange(ref _tickCount, 0);
            Volatile.Write(ref _simulationTimeSeconds, 0.0);

            foreach (var backend in _router.Backends)
            {
                await backend.ResetAsync(cancellationToken);
            }

            _registry.ResetAllRuntime();
            _log.Log(TrainControllerLogLevel.Info, "Simulation", null, "Test simulation reset (all trains).");
        }
        finally
        {
            _gate.Release();
        }
    }
}
