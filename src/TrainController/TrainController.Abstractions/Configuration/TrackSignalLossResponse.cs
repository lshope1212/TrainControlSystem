namespace TrainController.Abstractions.Configuration;

/// <summary>
/// How the controller brakes when the track-circuit signal (valid authorized speed /
/// authority) is lost. The project requirement only states that such a loss must result in
/// braking; it does not specify which brake.
/// </summary>
public enum TrackSignalLossResponse
{
    /// <summary>
    /// PROVISIONAL default. Target speed 0: the service brake stops the train (overspeed
    /// protection against a 0 limit) and holds it once stopped. Clears when the signal returns.
    /// </summary>
    ServiceBrakeStop = 0,

    /// <summary>
    /// Latched emergency brake (<see cref="Outputs.EmergencyBrakeCause.TrackSignalLoss"/>).
    /// Driver reset is refused while the signal is still lost.
    /// </summary>
    EmergencyBrake
}
