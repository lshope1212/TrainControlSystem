using TrainController.Abstractions.Configuration;
using TrainController.Abstractions.Inputs;

namespace TrainController.Integration.State;

/// <summary>
/// Editable Driver state for ONE train, written by the Main UI and read by the tick loop.
/// Thread-safe; <see cref="Changed"/> is raised on the writing thread, outside the lock.
/// </summary>
/// <remarks>
/// Emergency-brake, emergency-brake-reset and announcements are one-shot: they stay pending
/// until the next controller tick for this train consumes them via <see cref="TakeForTick"/>.
/// </remarks>
public sealed class DriverInputState
{
    private readonly object _gate = new object();

    private OperatingMode _mode;
    private double _requestedSpeedMetersPerSecond;
    private bool _serviceBrakeRequested;
    private bool _emergencyBrakePressPending;
    private bool _emergencyBrakeResetPending;
    private string _announcementPending = string.Empty;
    private bool _leftDoorsOpenRequested;
    private bool _rightDoorsOpenRequested;
    private bool _exteriorLightsRequested;
    private double _cabinTemperatureSetpointCelsius;

    /// <param name="defaults">PROVISIONAL startup values; see <see cref="ControllerStartupDefaults"/>.</param>
    public DriverInputState(ControllerStartupDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        _mode = defaults.InitialOperatingMode;
        _cabinTemperatureSetpointCelsius = defaults.InitialCabinTemperatureSetpointCelsius;
    }

    public event EventHandler? Changed;

    public OperatingMode Mode
    {
        get { lock (_gate) { return _mode; } }
        set => Set(ref _mode, value);
    }

    /// <exception cref="ArgumentOutOfRangeException">Negative, NaN or infinite.</exception>
    public double RequestedSpeedMetersPerSecond
    {
        get { lock (_gate) { return _requestedSpeedMetersPerSecond; } }
        set
        {
            if (!double.IsFinite(value) || value < 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Requested speed must be finite and >= 0.");
            }

            Set(ref _requestedSpeedMetersPerSecond, value);
        }
    }

    public bool ServiceBrakeRequested
    {
        get { lock (_gate) { return _serviceBrakeRequested; } }
        set => Set(ref _serviceBrakeRequested, value);
    }

    public bool LeftDoorsOpenRequested
    {
        get { lock (_gate) { return _leftDoorsOpenRequested; } }
        set => Set(ref _leftDoorsOpenRequested, value);
    }

    public bool RightDoorsOpenRequested
    {
        get { lock (_gate) { return _rightDoorsOpenRequested; } }
        set => Set(ref _rightDoorsOpenRequested, value);
    }

    public bool ExteriorLightsRequested
    {
        get { lock (_gate) { return _exteriorLightsRequested; } }
        set => Set(ref _exteriorLightsRequested, value);
    }

    /// <exception cref="ArgumentOutOfRangeException">NaN or infinite.</exception>
    public double CabinTemperatureSetpointCelsius
    {
        get { lock (_gate) { return _cabinTemperatureSetpointCelsius; } }
        set
        {
            if (!double.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Cabin setpoint must be finite.");
            }

            Set(ref _cabinTemperatureSetpointCelsius, value);
        }
    }

    public bool EmergencyBrakePressPending
    {
        get { lock (_gate) { return _emergencyBrakePressPending; } }
    }

    public bool EmergencyBrakeResetPending
    {
        get { lock (_gate) { return _emergencyBrakeResetPending; } }
    }

    /// <summary>Announcement queued for this train's next tick (empty = none).</summary>
    public string AnnouncementPending
    {
        get { lock (_gate) { return _announcementPending; } }
    }

    /// <summary>
    /// Queues a one-shot driver announcement, sent once on this train's next tick. Blank text is
    /// ignored. A later request before that tick replaces the earlier one.
    /// </summary>
    public void RequestAnnouncement(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length > 0)
        {
            Set(ref _announcementPending, trimmed);
        }
    }

    /// <summary>Driver presses the emergency brake (latched by the controller on the next tick).</summary>
    public void PressEmergencyBrake() => Set(ref _emergencyBrakePressPending, true);

    /// <summary>Driver presses emergency-brake reset (honored by the controller only if no emergency remains).</summary>
    public void PressEmergencyBrakeReset() => Set(ref _emergencyBrakeResetPending, true);

    /// <summary>Current driver input WITHOUT consuming pending presses (for display).</summary>
    public DriverInput Peek()
    {
        lock (_gate)
        {
            return Build();
        }
    }

    /// <summary>Driver input for one controller tick; consumes pending one-shot presses.</summary>
    public DriverInput TakeForTick()
    {
        bool consumed;
        DriverInput snapshot;

        lock (_gate)
        {
            snapshot = Build();
            consumed = _emergencyBrakePressPending || _emergencyBrakeResetPending || _announcementPending.Length > 0;
            _emergencyBrakePressPending = false;
            _emergencyBrakeResetPending = false;
            _announcementPending = string.Empty;
        }

        if (consumed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return snapshot;
    }

    /// <summary>Drops pending presses (part of Test Simulation Reset). Held settings are kept.</summary>
    public void ClearPendingPresses()
    {
        bool cleared;

        lock (_gate)
        {
            cleared = _emergencyBrakePressPending || _emergencyBrakeResetPending || _announcementPending.Length > 0;
            _emergencyBrakePressPending = false;
            _emergencyBrakeResetPending = false;
            _announcementPending = string.Empty;
        }

        if (cleared)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private DriverInput Build() => new DriverInput
    {
        Mode = _mode,
        RequestedSpeedMetersPerSecond = _requestedSpeedMetersPerSecond,
        ServiceBrakeRequested = _serviceBrakeRequested,
        EmergencyBrakeRequested = _emergencyBrakePressPending,
        EmergencyBrakeResetRequested = _emergencyBrakeResetPending,
        LeftDoorsOpenRequested = _leftDoorsOpenRequested,
        RightDoorsOpenRequested = _rightDoorsOpenRequested,
        ExteriorLightsRequested = _exteriorLightsRequested,
        CabinTemperatureSetpointCelsius = _cabinTemperatureSetpointCelsius,
        AnnouncementRequest = _announcementPending,
    };

    private void Set<T>(ref T field, T value)
    {
        bool changed;

        lock (_gate)
        {
            changed = !EqualityComparer<T>.Default.Equals(field, value);
            field = value;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
