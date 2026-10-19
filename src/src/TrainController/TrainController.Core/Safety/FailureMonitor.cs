using TrainController.Abstractions.Configuration;
using TrainController.Abstractions.Outputs;

namespace TrainController.Core.Safety;

/// <summary>Result of one <see cref="FailureMonitor.Evaluate"/> call.</summary>
public readonly record struct FailureEvaluation(
    ControllerFailure Latched,
    ControllerFailure NewlyDetected,
    ControllerFailure Cleared,
    bool ResetRejected,
    string ResetRejectedReason,
    string ResetBlockedReason);

/// <summary>
/// Infers Signal Pickup, Brake and Engine failures from normal observable data only (no Train
/// Model failure flags) and keeps them LATCHED until a driver "Reset failures" whose condition
/// is met. All thresholds come from <see cref="ControllerPolicy"/>.
/// </summary>
/// <remarks>
/// <para>
/// The speed measured on this tick is the response to the commands sent on the PREVIOUS tick, so
/// brake / engine detection uses the previous tick's commands (<see cref="RecordCommands"/>) and
/// compares the speed at the start of the detection window with the current speed:
/// </para>
/// <list type="bullet">
/// <item>Signal pickup: <c>TrackSignalValid == false</c> for <c>SignalPickupFailureLatchSeconds</c>
///       in a row (5 ticks at 0.1 s). Reset: signal valid again.</item>
/// <item>Brake: service brake commanded, emergency brake not, speed above
///       <c>BrakeFailureMinimumSpeedMetersPerSecond</c>, continuously for
///       <c>BrakeFailureDetectionSeconds</c>, and the average deceleration over that window is below
///       <c>BrakeFailureMinimumDecelerationMetersPerSecondSquared</c>. Reset: train stopped.</item>
/// <item>Engine: no brake commanded, setpoint − speed above
///       <c>EngineFailureMinimumSpeedErrorMetersPerSecond</c>, commanded power at least
///       <c>EngineFailureMinimumPowerFraction</c> × rated power, continuously for
///       <c>EngineFailureDetectionSeconds</c>, and the average acceleration over that window is at most
///       <c>EngineFailureMaximumAccelerationMetersPerSecondSquared</c>. Reset: train stopped.</item>
/// </list>
/// <para>A window that completes WITHOUT detecting a failure starts again on the next tick.</para>
/// <para>The Raspberry Pi Hardware controller implements the same rules independently.</para>
/// </remarks>
public sealed class FailureMonitor
{
    private const double TimeEpsilon = 1e-9;

    private ControllerFailure _latched;
    private double _signalInvalidSeconds;

    private bool _brakeWindowActive;
    private double _brakeWindowStartSpeed;
    private double _brakeWindowSeconds;

    private bool _engineWindowActive;
    private double _engineWindowStartSpeed;
    private double _engineWindowSeconds;

    private bool _hasPrevious;
    private double _previousSpeed;
    private bool _previousServiceBrake;
    private bool _previousEmergencyBrake;
    private double _previousPowerWatts;
    private double _previousSetpoint;

    public ControllerFailure Latched => _latched;

    public FailureEvaluation Evaluate(
        double speedMetersPerSecond,
        bool trackSignalValid,
        bool stopped,
        double deltaTimeSeconds,
        bool resetRequested,
        double ratedPowerWatts,
        ControllerPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // 1. Driver "Reset failures": each latched failure clears only if its condition is met.
        var cleared = ControllerFailure.None;
        var rejected = new List<string>();
        if (resetRequested && _latched != ControllerFailure.None)
        {
            if (_latched.HasFlag(ControllerFailure.SignalPickup))
            {
                if (trackSignalValid) cleared |= ControllerFailure.SignalPickup;
                else rejected.Add("signal pickup failure: track signal still invalid");
            }

            if (_latched.HasFlag(ControllerFailure.Brake))
            {
                if (stopped) cleared |= ControllerFailure.Brake;
                else rejected.Add("brake failure: train still moving");
            }

            if (_latched.HasFlag(ControllerFailure.Engine))
            {
                if (stopped) cleared |= ControllerFailure.Engine;
                else rejected.Add("engine failure: train still moving");
            }

            _latched &= ~cleared;
            if (cleared.HasFlag(ControllerFailure.Brake)) _brakeWindowActive = false;
            if (cleared.HasFlag(ControllerFailure.Engine)) _engineWindowActive = false;
        }

        var before = _latched;
        var dt = deltaTimeSeconds;

        // 2. Signal pickup.
        if (!trackSignalValid)
        {
            _signalInvalidSeconds += dt;
            if (_signalInvalidSeconds + TimeEpsilon >= policy.SignalPickupFailureLatchSeconds)
            {
                _latched |= ControllerFailure.SignalPickup;
            }
        }
        else
        {
            _signalInvalidSeconds = 0.0;
        }

        // 3. Brake (service brake only; the emergency brake is assumed to always work).
        var brakeCondition = _hasPrevious
            && _previousServiceBrake
            && !_previousEmergencyBrake
            && _previousSpeed > policy.BrakeFailureMinimumSpeedMetersPerSecond;
        if (!_latched.HasFlag(ControllerFailure.Brake) && brakeCondition)
        {
            if (!_brakeWindowActive)
            {
                _brakeWindowActive = true;
                _brakeWindowStartSpeed = _previousSpeed;
                _brakeWindowSeconds = 0.0;
            }

            _brakeWindowSeconds += dt;
            if (_brakeWindowSeconds + TimeEpsilon >= policy.BrakeFailureDetectionSeconds)
            {
                var deceleration = (_brakeWindowStartSpeed - speedMetersPerSecond) / _brakeWindowSeconds;
                if (deceleration < policy.BrakeFailureMinimumDecelerationMetersPerSecondSquared)
                {
                    _latched |= ControllerFailure.Brake;
                }

                _brakeWindowActive = false;
            }
        }
        else
        {
            _brakeWindowActive = false;
        }

        // 4. Engine (propulsion).
        var engineCondition = _hasPrevious
            && !_previousServiceBrake
            && !_previousEmergencyBrake
            && _previousSetpoint - _previousSpeed > policy.EngineFailureMinimumSpeedErrorMetersPerSecond
            && _previousPowerWatts >= policy.EngineFailureMinimumPowerFraction * ratedPowerWatts;
        if (!_latched.HasFlag(ControllerFailure.Engine) && engineCondition)
        {
            if (!_engineWindowActive)
            {
                _engineWindowActive = true;
                _engineWindowStartSpeed = _previousSpeed;
                _engineWindowSeconds = 0.0;
            }

            _engineWindowSeconds += dt;
            if (_engineWindowSeconds + TimeEpsilon >= policy.EngineFailureDetectionSeconds)
            {
                var acceleration = (speedMetersPerSecond - _engineWindowStartSpeed) / _engineWindowSeconds;
                if (acceleration <= policy.EngineFailureMaximumAccelerationMetersPerSecondSquared)
                {
                    _latched |= ControllerFailure.Engine;
                }

                _engineWindowActive = false;
            }
        }
        else
        {
            _engineWindowActive = false;
        }

        // Reason a reset would be rejected right now (for the UI).
        var blocked = new List<string>();
        if (_latched.HasFlag(ControllerFailure.SignalPickup) && !trackSignalValid) blocked.Add("track signal still invalid");
        if ((_latched & (ControllerFailure.Brake | ControllerFailure.Engine)) != 0 && !stopped) blocked.Add("train still moving");

        return new FailureEvaluation(
            _latched,
            _latched & ~before,
            cleared,
            rejected.Count > 0,
            string.Join("; ", rejected),
            string.Join("; ", blocked));
    }

    /// <summary>Records what was commanded this tick; judged against next tick's speed.</summary>
    public void RecordCommands(double speedMetersPerSecond, bool serviceBrake, bool emergencyBrake, double powerWatts, double setpointMetersPerSecond)
    {
        _hasPrevious = true;
        _previousSpeed = speedMetersPerSecond;
        _previousServiceBrake = serviceBrake;
        _previousEmergencyBrake = emergencyBrake;
        _previousPowerWatts = powerWatts;
        _previousSetpoint = setpointMetersPerSecond;
    }

    /// <summary>A fail-safe tick: there is no meaningful command to judge next tick.</summary>
    public void ForgetCommands()
    {
        _hasPrevious = false;
        _brakeWindowActive = false;
        _engineWindowActive = false;
    }

    public void Reset()
    {
        _latched = ControllerFailure.None;
        _signalInvalidSeconds = 0.0;
        _brakeWindowActive = false;
        _engineWindowActive = false;
        _hasPrevious = false;
    }
}
