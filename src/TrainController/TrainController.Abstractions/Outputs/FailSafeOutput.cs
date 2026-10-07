using TrainController.Abstractions.Configuration;
using TrainController.Abstractions.Inputs;

namespace TrainController.Abstractions.Outputs;

/// <summary>
/// Builds the output used whenever a real controller output is unavailable or untrustworthy
/// (Hardware communication failure, invalid input, invalid controller output, controller
/// exception).
/// </summary>
/// <remarks>
/// <para>Traction and braking: power 0 and emergency brake applied, always.</para>
/// <para>
/// Doors (safety-related, so NOT carried over from previous door commands): the door/motion
/// interlock still applies. A door may only remain commanded open if the last known Train
/// Model state shows the train stopped (≤ <see cref="ControllerPolicy.StoppedSpeedThresholdMetersPerSecond"/>)
/// AND that door already physically open — so passengers are not shut in on a stationary
/// train. In every other case (moving, speed unknown/invalid, no model state, door closed)
/// both sides are commanded closed. The fail-safe never opens a door.
/// </para>
/// <para>
/// Non-safety settings (exterior lights, cabin setpoint) and the driver's station
/// information are retained from the previous output, if any.
/// </para>
/// </remarks>
public static class FailSafeOutput
{
    public static TrainControllerOutput Create(
        string trainId,
        long tickId,
        EmergencyBrakeCause cause,
        string reason,
        TrainModelInput? lastKnownModelInput,
        ControllerPolicy policy,
        TrainControllerOutput? previous = null)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var previousCommands = previous?.Commands ?? new TrainModelCommand();
        var previousDisplay = previous?.Display ?? new DriverDisplayState();

        var stopped = IsKnownStopped(lastKnownModelInput, policy);
        var leftOpen = stopped && lastKnownModelInput!.LeftDoorsOpen;
        var rightOpen = stopped && lastKnownModelInput!.RightDoorsOpen;

        var alerts = new List<string> { reason };
        if (!stopped)
        {
            alerts.Add("Fail-safe: doors commanded closed (train not known to be stopped).");
        }

        return new TrainControllerOutput
        {
            TrainId = trainId,
            TickId = tickId,
            IsFailSafe = true,
            Commands = new TrainModelCommand
            {
                PowerCommandWatts = 0.0,
                ServiceBrakeCommand = false,
                EmergencyBrakeCommand = true,
                LeftDoorsOpenCommand = leftOpen,
                RightDoorsOpenCommand = rightOpen,
                ExteriorLightsCommand = previousCommands.ExteriorLightsCommand,
                CabinTemperatureSetpointCelsius = previousCommands.CabinTemperatureSetpointCelsius,
                StationAnnouncement = string.Empty,
            },
            Display = previousDisplay with
            {
                EffectiveTargetSpeedMetersPerSecond = 0.0,
                StationBrakingActive = false,
                StationBrakingAdvised = false,
                DoorInterlockActive = !stopped,
                EmergencyBrakeResetRejected = false,
                BeaconRecalibrated = false,
                AuthorityRecalibrated = false,
                StationEvent = StationEvent.None,
                TractionState = TractionState.FailSafe,
                EmergencyBrakeCauses = previousDisplay.EmergencyBrakeCauses | cause,
                ControllerFaulted = true,
                FaultReason = reason,
                Alerts = alerts,
            },
        };
    }

    private static bool IsKnownStopped(TrainModelInput? model, ControllerPolicy policy)
    {
        if (model is null)
        {
            return false;
        }

        var speed = model.ActualSpeedMetersPerSecond;
        return double.IsFinite(speed) && speed >= 0.0 && speed <= policy.StoppedSpeedThresholdMetersPerSecond;
    }
}
