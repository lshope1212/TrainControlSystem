using TrainControl.Contracts.Enums;
using TrainController.Abstractions.Configuration;
using TrainController.Abstractions.Inputs;

namespace TrainController.Integration.State;

/// <summary>
/// Test-Mode stand-in for the Train Model, for ONE train. Edited through the Test UI and
/// used as that train's model input for every tick while (global) Test Mode is on.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsActive"/> is the Train Model's "dispatched / in service" state as simulated by
/// the tester. The Train Controller does not own dispatch state: in Normal Mode the value
/// comes from the real Train Model; in Test Mode it comes only from here. The initial value
/// is false (a fresh simulated train is not in service) — an initial state, not a rule.
/// </para>
/// <para>
/// Setters deliberately accept unusual or invalid values (e.g. speed above the vehicle
/// maximum, negative distance) so edge and fault cases can be injected; the input validator
/// and the controllers deal with them.
/// </para>
/// <para>
/// Editing beacon fields does not by itself constitute a beacon reception.
/// <see cref="TransmitBeacon"/> simulates the train receiving a beacon: the NEXT tick for this
/// train reports it as newly received (exactly once), which makes the controller recalibrate.
/// This one-shot mechanism is internal to the Test UI adapter, not an external contract.
/// </para>
/// </remarks>
public sealed class TestModelState
{
    private readonly object _gate = new object();

    private bool _isActive;
    private double _actualSpeedMetersPerSecond;
    private double _authorizedSpeedMetersPerSecond;
    private double _remainingAuthorityMeters;
    private bool _trackSignalValid = true;
    private bool _beaconValid;
    private bool _beaconReceptionPending;
    private bool _authorityUpdatePending;
    private string _nextStationName = string.Empty;
    private double _distanceToNextStationMeters;
    private PlatformSide _platformSide = PlatformSide.None;
    private bool _passengerEmergencyBrakeRequested;
    private bool _leftDoorsOpen;
    private bool _rightDoorsOpen;
    private bool _exteriorLightsOn;
    private double _cabinTemperatureCelsius;

    /// <param name="defaults">PROVISIONAL startup values; see <see cref="ControllerStartupDefaults"/>.</param>
    public TestModelState(ControllerStartupDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        _cabinTemperatureCelsius = defaults.InitialTestCabinTemperatureCelsius;
    }

    public event EventHandler? Changed;

    public bool IsActive { get { lock (_gate) { return _isActive; } } set => Set(ref _isActive, value); }

    public double ActualSpeedMetersPerSecond { get { lock (_gate) { return _actualSpeedMetersPerSecond; } } set => Set(ref _actualSpeedMetersPerSecond, value); }

    public double AuthorizedSpeedMetersPerSecond { get { lock (_gate) { return _authorizedSpeedMetersPerSecond; } } set => Set(ref _authorizedSpeedMetersPerSecond, value); }

    /// <summary>
    /// Authority value "sent" by the simulated Train Model. Changing it counts as a NEW authority
    /// update, reported once on this train's next tick (the controller recalibrates, then
    /// dead-reckons v·dt until the next update).
    /// </summary>
    public double RemainingAuthorityMeters
    {
        get { lock (_gate) { return _remainingAuthorityMeters; } }
        set
        {
            bool changed;
            lock (_gate)
            {
                changed = !_remainingAuthorityMeters.Equals(value);
                _remainingAuthorityMeters = value;
                if (changed)
                {
                    _authorityUpdatePending = true;
                }
            }

            if (changed)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>True from an authority change / <see cref="SendAuthorityUpdate"/> until the next tick consumes it.</summary>
    public bool AuthorityUpdatePending { get { lock (_gate) { return _authorityUpdatePending; } } }

    /// <summary>Re-sends the current authority value as a new update (e.g. the same value again).</summary>
    public void SendAuthorityUpdate()
    {
        lock (_gate)
        {
            _authorityUpdatePending = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool TrackSignalValid { get { lock (_gate) { return _trackSignalValid; } } set => Set(ref _trackSignalValid, value); }

    public bool BeaconValid { get { lock (_gate) { return _beaconValid; } } set => Set(ref _beaconValid, value); }

    /// <summary>True from <see cref="TransmitBeacon"/> until the next tick for this train consumes it.</summary>
    public bool BeaconReceptionPending { get { lock (_gate) { return _beaconReceptionPending; } } }

    public string NextStationName
    {
        get { lock (_gate) { return _nextStationName; } }
        set => Set(ref _nextStationName, value ?? string.Empty);
    }

    public double DistanceToNextStationMeters { get { lock (_gate) { return _distanceToNextStationMeters; } } set => Set(ref _distanceToNextStationMeters, value); }

    public PlatformSide PlatformSide { get { lock (_gate) { return _platformSide; } } set => Set(ref _platformSide, value); }

    public bool PassengerEmergencyBrakeRequested { get { lock (_gate) { return _passengerEmergencyBrakeRequested; } } set => Set(ref _passengerEmergencyBrakeRequested, value); }

    public bool LeftDoorsOpen { get { lock (_gate) { return _leftDoorsOpen; } } set => Set(ref _leftDoorsOpen, value); }

    public bool RightDoorsOpen { get { lock (_gate) { return _rightDoorsOpen; } } set => Set(ref _rightDoorsOpen, value); }

    public bool ExteriorLightsOn { get { lock (_gate) { return _exteriorLightsOn; } } set => Set(ref _exteriorLightsOn, value); }

    public double CabinTemperatureCelsius { get { lock (_gate) { return _cabinTemperatureCelsius; } } set => Set(ref _cabinTemperatureCelsius, value); }

    /// <summary>
    /// Simulates the train receiving a beacon carrying the current beacon field values.
    /// Marks the beacon valid; the next tick reports it as newly received.
    /// </summary>
    public void TransmitBeacon()
    {
        lock (_gate)
        {
            _beaconValid = true;
            _beaconReceptionPending = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Model-input snapshot WITHOUT consuming a pending beacon reception (for display).</summary>
    public TrainModelInput PeekModelInput()
    {
        lock (_gate)
        {
            return Build(_beaconReceptionPending, _authorityUpdatePending);
        }
    }

    /// <summary>Model input for one controller tick; consumes a pending beacon reception.</summary>
    public TrainModelInput TakeModelInputForTick()
    {
        bool consumed;
        TrainModelInput snapshot;

        lock (_gate)
        {
            snapshot = Build(_beaconReceptionPending, _authorityUpdatePending);
            consumed = _beaconReceptionPending || _authorityUpdatePending;
            _beaconReceptionPending = false;
            _authorityUpdatePending = false;
        }

        if (consumed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return snapshot;
    }

    /// <summary>Drops pending beacon / authority events (part of Test Simulation Reset). Entered values are kept.</summary>
    public void ClearPendingEvents()
    {
        bool cleared;

        lock (_gate)
        {
            cleared = _beaconReceptionPending || _authorityUpdatePending;
            _beaconReceptionPending = false;
            _authorityUpdatePending = false;
        }

        if (cleared)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private TrainModelInput Build(bool beaconNewlyReceived, bool authorityUpdated) => new TrainModelInput
    {
        IsActive = _isActive,
        ActualSpeedMetersPerSecond = _actualSpeedMetersPerSecond,
        AuthorizedSpeedMetersPerSecond = _authorizedSpeedMetersPerSecond,
        RemainingAuthorityMeters = _remainingAuthorityMeters,
        AuthorityUpdateReceived = authorityUpdated,
        TrackSignalValid = _trackSignalValid,
        Beacon = new BeaconData
        {
            IsValid = _beaconValid,
            IsNewlyReceived = beaconNewlyReceived,
            NextStationName = _nextStationName,
            DistanceToStationMeters = _distanceToNextStationMeters,
            PlatformSide = _platformSide,
        },
        PassengerEmergencyBrakeRequested = _passengerEmergencyBrakeRequested,
        LeftDoorsOpen = _leftDoorsOpen,
        RightDoorsOpen = _rightDoorsOpen,
        ExteriorLightsOn = _exteriorLightsOn,
        CabinTemperatureCelsius = _cabinTemperatureCelsius,
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
