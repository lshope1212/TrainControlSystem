using TrainController.Abstractions.Configuration;
using TrainController.Abstractions.Fleet;
using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;

namespace TrainController.Integration.State;

/// <summary>
/// Everything the Train Controller subsystem keeps for ONE train on the Windows side:
/// Driver state, Engineer settings, Test Model state and the latest tick result.
/// Controller-internal runtime state (PI integral, latches, station tracking) lives in the
/// backend that serves the train (Software Core instance or the Pi), also per train.
/// </summary>
/// <remarks>
/// Deliberately has no IsRunning / dispatch flag: whether a train runs is read from its model
/// input (Train Model in Normal Mode, <see cref="TestModel"/> in Test Mode) on every tick.
/// </remarks>
public sealed class TrainRuntimeSlot
{
    private readonly object _gate = new object();
    private EngineerSettings _engineer = EngineerSettings.Default;
    private TrainControllerOutput? _lastOutput;
    private TrainModelInput? _lastModelInput;
    private string _lastAnnouncement = string.Empty;
    private long _lastAnnouncementTick;

    internal TrainRuntimeSlot(string trainId, ControllerStartupDefaults defaults)
    {
        TrainId = trainId;
        ControllerType = TrainFleet.GetControllerType(trainId);
        Driver = new DriverInputState(defaults);
        TestModel = new TestModelState(defaults);
    }

    public string TrainId { get; }

    /// <summary>Fixed assignment from <see cref="TrainFleet"/>; read-only by design.</summary>
    public ControllerType ControllerType { get; }

    public DriverInputState Driver { get; }

    /// <summary>This train's Train Model stand-in, used as model input only while Test Mode is on.</summary>
    public TestModelState TestModel { get; }

    /// <summary>Raised after Engineer settings change.</summary>
    public event EventHandler? EngineerSettingsChanged;

    /// <summary>Raised after a tick result is recorded or runtime state is reset.</summary>
    public event EventHandler? OutputChanged;

    public EngineerSettings Engineer
    {
        get { lock (_gate) { return _engineer; } }
    }

    /// <summary>Latest controller output for this train; null before the first tick / after Reset.</summary>
    public TrainControllerOutput? LastOutput
    {
        get { lock (_gate) { return _lastOutput; } }
    }

    /// <summary>Model input (from Train Model or Test UI) used for the latest tick.</summary>
    public TrainModelInput? LastModelInput
    {
        get { lock (_gate) { return _lastModelInput; } }
    }

    /// <summary>
    /// Most recent non-empty station announcement sent for this train (display only: the
    /// command itself is non-empty on a single tick). Empty until the first announcement / after Reset.
    /// </summary>
    public string LastAnnouncement
    {
        get { lock (_gate) { return _lastAnnouncement; } }
    }

    public long LastAnnouncementTick
    {
        get { lock (_gate) { return _lastAnnouncementTick; } }
    }

    /// <exception cref="ArgumentOutOfRangeException">A gain is negative, NaN or infinite; settings unchanged.</exception>
    public void SetEngineerSettings(double kp, double ki)
    {
        var settings = EngineerSettings.Create(kp, ki);

        lock (_gate)
        {
            _engineer = settings;
        }

        EngineerSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <exception cref="ArgumentException">The output belongs to another train.</exception>
    public void RecordTickResult(TrainModelInput modelInput, TrainControllerOutput output)
    {
        ArgumentNullException.ThrowIfNull(modelInput);
        ArgumentNullException.ThrowIfNull(output);

        if (!string.Equals(output.TrainId, TrainId, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Output for '{output.TrainId}' cannot be recorded on '{TrainId}'.", nameof(output));
        }

        lock (_gate)
        {
            _lastModelInput = modelInput;
            _lastOutput = output;
            if (!string.IsNullOrEmpty(output.Commands.StationAnnouncement))
            {
                _lastAnnouncement = output.Commands.StationAnnouncement;
                _lastAnnouncementTick = output.TickId;
            }
        }

        OutputChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Test Simulation Reset for this train's Windows-side runtime state. Keeps Engineer
    /// settings, Driver held settings and every Test Model input value.
    /// </summary>
    public void ResetRuntime()
    {
        lock (_gate)
        {
            _lastOutput = null;
            _lastModelInput = null;
            _lastAnnouncement = string.Empty;
            _lastAnnouncementTick = 0;
        }

        Driver.ClearPendingPresses();
        TestModel.ClearPendingEvents();
        OutputChanged?.Invoke(this, EventArgs.Empty);
    }
}
