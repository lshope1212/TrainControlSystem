using System.Diagnostics;
using TrainController.Integration.Configuration;
using TrainController.Integration.Logging;
using TrainController.Integration.ModelInput;

namespace TrainController.Integration.Simulation;

public enum TestSimulationState
{
    Stopped = 0,
    Running
}

/// <summary>
/// GLOBAL Train Controller test-simulation controls: Run / Stop / Step / Reset / Speed.
/// They act on the whole subsystem, never on one train. Only available in Test Mode.
/// </summary>
/// <remarks>
/// <para>
/// Speed multiplier changes only how often ticks run in wall-clock time
/// (interval = timestep / multiplier). The simulated timestep given to controllers is
/// always the same fixed value.
/// </para>
/// <para>
/// Stop lets the tick in progress finish, then freezes; all runtime state is preserved and
/// a later Run or Step continues from it. Leaving Test Mode stops the simulation after the
/// current tick. Network-free: awaiting here never blocks the UI thread.
/// </para>
/// </remarks>
public sealed class TestSimulationController
{
    private const string Category = "Simulation";

    private readonly TrainControllerTickEngine _engine;
    private readonly TestModeController _testMode;
    private readonly TrainControllerRuntimeOptions _options;
    private readonly ITrainControllerEventLog _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new object();
    private CancellationTokenSource? _runCancellation;
    private Task _runLoop = Task.CompletedTask;
    private TestSimulationState _state;
    private int _speedMultiplier;

    /// <param name="delay">Wall-clock wait between ticks; injectable for tests. Defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public TestSimulationController(
        TrainControllerTickEngine engine,
        TestModeController testMode,
        TrainControllerRuntimeOptions options,
        ITrainControllerEventLog? log = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _testMode = testMode ?? throw new ArgumentNullException(nameof(testMode));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _log = log ?? NullTrainControllerEventLog.Instance;
        _delay = delay ?? ((interval, token) => Task.Delay(interval, token));
        _speedMultiplier = _options.SupportedSpeedMultipliers[0];
    }

    public event EventHandler? StateChanged;

    public event EventHandler<TickReport>? TickCompleted;

    public TestSimulationState State
    {
        get { lock (_gate) { return _state; } }
    }

    public IReadOnlyList<int> SupportedSpeedMultipliers => _options.SupportedSpeedMultipliers;

    public int SpeedMultiplier
    {
        get { lock (_gate) { return _speedMultiplier; } }
    }

    /// <summary>Fixed simulated timestep; independent of the speed multiplier.</summary>
    public double DeltaTimeSeconds => _engine.DeltaTimeSeconds;

    /// <summary>Wall-clock time between tick starts while running.</summary>
    public TimeSpan WallClockTickInterval => TimeSpan.FromSeconds(_engine.DeltaTimeSeconds / SpeedMultiplier);

    /// <exception cref="ArgumentOutOfRangeException">Not one of <see cref="SupportedSpeedMultipliers"/>.</exception>
    public void SetSpeedMultiplier(int multiplier)
    {
        if (!_options.SupportedSpeedMultipliers.Contains(multiplier))
        {
            throw new ArgumentOutOfRangeException(nameof(multiplier), multiplier,
                $"Supported multipliers: {string.Join(", ", _options.SupportedSpeedMultipliers)}.");
        }

        lock (_gate)
        {
            _speedMultiplier = multiplier;
        }

        _log.Log(TrainControllerLogLevel.Info, Category, null, $"Simulation speed set to {multiplier}x.");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Starts continuous ticking. Returns false if already running or not in Test Mode.</summary>
    public bool Run()
    {
        if (!_testMode.IsTestMode)
        {
            _log.Log(TrainControllerLogLevel.Warning, Category, null, "Run ignored: Test Mode is off.");
            return false;
        }

        lock (_gate)
        {
            if (_state == TestSimulationState.Running)
            {
                return false;
            }

            _state = TestSimulationState.Running;
            _runCancellation = new CancellationTokenSource();
            _runLoop = RunLoopAsync(_runCancellation.Token);
        }

        _log.Log(TrainControllerLogLevel.Info, Category, null, "Test simulation running.");
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Stops after the tick in progress; preserves all state. No-op when stopped.</summary>
    public async Task StopAsync()
    {
        Task loop;
        CancellationTokenSource? cancellation;

        lock (_gate)
        {
            loop = _runLoop;
            cancellation = _runCancellation;
        }

        cancellation?.Cancel();
        await loop;
    }

    /// <summary>Executes exactly one tick for all active trains. Returns null if running or not in Test Mode.</summary>
    public async Task<TickReport?> StepAsync(CancellationToken cancellationToken = default)
    {
        if (!_testMode.IsTestMode || State == TestSimulationState.Running)
        {
            _log.Log(TrainControllerLogLevel.Warning, Category, null, "Step ignored: requires Test Mode and a stopped simulation.");
            return null;
        }

        var report = await _engine.ExecuteTickAsync(cancellationToken);
        TickCompleted?.Invoke(this, report);
        return report;
    }

    /// <summary>Stops, then resets the whole test runtime for all ten trains.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync();
        await _engine.ResetAsync(cancellationToken);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        // Yield first so Run() returns to its caller before the first tick executes.
        await Task.Yield();

        try
        {
            while (!cancellationToken.IsCancellationRequested && _testMode.IsTestMode)
            {
                var started = Stopwatch.GetTimestamp();

                // Not cancellable: a started tick always completes, so Stop freezes between ticks.
                var report = await _engine.ExecuteTickAsync(CancellationToken.None);
                TickCompleted?.Invoke(this, report);

                var remaining = WallClockTickInterval - Stopwatch.GetElapsedTime(started);
                if (remaining > TimeSpan.Zero)
                {
                    await _delay(remaining, cancellationToken);
                }
                else
                {
                    await Task.Yield();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Log(TrainControllerLogLevel.Error, Category, null, $"Test simulation stopped by an unexpected error: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _state = TestSimulationState.Stopped;

                // Not disposed: StopAsync may still call Cancel() on it concurrently, and a
                // CancellationTokenSource without timers holds no unmanaged resources.
                _runCancellation = null;
            }

            _log.Log(TrainControllerLogLevel.Info, Category, null, "Test simulation stopped.");
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
