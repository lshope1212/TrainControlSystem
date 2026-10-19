using TrainController.Integration.Logging;

namespace TrainController.Integration.ModelInput;

/// <summary>
/// The single, GLOBAL Test Mode switch. It selects which provider supplies model input for
/// ALL ten trains: the real Train Model (Normal) or the Test UI test state (Test). There is
/// no per-train mode and the two providers are never used together.
/// </summary>
public sealed class TestModeController
{
    private readonly object _gate = new object();
    private readonly IModelInputProvider _normal;
    private readonly IModelInputProvider _test;
    private readonly ITrainControllerEventLog _log;
    private bool _isTestMode;
    private bool _simplePhysicsEnabled;

    public TestModeController(IModelInputProvider normalProvider, IModelInputProvider testProvider, ITrainControllerEventLog? log = null)
    {
        _normal = normalProvider ?? throw new ArgumentNullException(nameof(normalProvider));
        _test = testProvider ?? throw new ArgumentNullException(nameof(testProvider));
        _log = log ?? NullTrainControllerEventLog.Instance;
    }

    public event EventHandler? TestModeChanged;

    public bool IsTestMode
    {
        get { lock (_gate) { return _isTestMode; } }
    }

    /// <summary>The ONE provider currently supplying model input.</summary>
    /// <summary>
    /// Test Mode only: when true, each Test Model state is moved by <see cref="Simulation.SimplePlantModel"/>
    /// after every tick (speed, doors, lights follow the commands). When false, the tester sets
    /// the actual speed by hand (used to reproduce brake / engine failures). Default false; the
    /// WPF composition root turns it on.
    /// </summary>
    public bool SimplePhysicsEnabled
    {
        get { lock (_gate) { return _simplePhysicsEnabled; } }
    }

    public event EventHandler? SimplePhysicsChanged;

    public void SetSimplePhysics(bool enabled)
    {
        lock (_gate)
        {
            if (_simplePhysicsEnabled == enabled)
            {
                return;
            }

            _simplePhysicsEnabled = enabled;
        }

        _log.Log(TrainControllerLogLevel.Info, "TestMode", null, enabled ? "Simple physics enabled (Test UI)." : "Simple physics disabled: actual speed is set by hand.");
        SimplePhysicsChanged?.Invoke(this, EventArgs.Empty);
    }

    public IModelInputProvider CurrentProvider
    {
        get { lock (_gate) { return _isTestMode ? _test : _normal; } }
    }

    /// <summary>Atomically reads the mode and its provider together (used at the start of each tick).</summary>
    public (bool IsTestMode, IModelInputProvider Provider) Capture()
    {
        lock (_gate)
        {
            return (_isTestMode, _isTestMode ? _test : _normal);
        }
    }

    public void SetTestMode(bool enabled)
    {
        lock (_gate)
        {
            if (_isTestMode == enabled)
            {
                return;
            }

            _isTestMode = enabled;
        }

        _log.Log(TrainControllerLogLevel.Info, "TestMode", null, enabled ? "Test Mode enabled." : "Test Mode disabled.");
        TestModeChanged?.Invoke(this, EventArgs.Empty);
    }
}
