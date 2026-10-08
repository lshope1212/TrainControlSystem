namespace TrainController.Abstractions.Inputs;

/// <summary>Driver-selected control mode.</summary>
/// <remarks>
/// The mode a train starts in is NOT defined by any project source; it is configured by
/// <see cref="Configuration.ControllerStartupDefaults.InitialOperatingMode"/>.
/// </remarks>
public enum OperatingMode
{
    /// <summary>
    /// Controller tracks the driver's requested speed (capped by authorized speed); station
    /// braking guidance is advisory and the driver brakes. Safety protections stay automatic.
    /// </summary>
    Manual = 0,

    /// <summary>
    /// Controller tracks the authorized speed and performs station braking itself.
    /// </summary>
    Automatic
}
