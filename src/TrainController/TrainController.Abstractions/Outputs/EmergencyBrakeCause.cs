namespace TrainController.Abstractions.Outputs;

/// <summary>
/// Active emergency-brake conditions. Several may be active at once; the emergency brake
/// stays applied until ALL of them are cleared (and, for latched causes, the driver resets).
/// </summary>
[Flags]
public enum EmergencyBrakeCause
{
    None = 0,

    /// <summary>Driver pressed the emergency brake (latched until a valid reset).</summary>
    Driver = 1 << 0,

    /// <summary>Passenger emergency brake request from the Train Model.</summary>
    Passenger = 1 << 1,

    /// <summary>Raspberry Pi communication lost / invalid (Hardware trains only).</summary>
    HardwareCommunication = 1 << 2,

    /// <summary>Train cannot stop within the remaining authority, or has exhausted it.</summary>
    AuthorityViolation = 1 << 3,

    /// <summary>Controller received an input that failed validation.</summary>
    InvalidInput = 1 << 4,

    /// <summary>Internal controller fault (exception, invalid output, ...).</summary>
    ControllerFault = 1 << 5,

    /// <summary>
    /// Track signal lost, when <see cref="Configuration.TrackSignalLossResponse.EmergencyBrake"/>
    /// is configured (not the default).
    /// </summary>
    TrackSignalLoss = 1 << 6,
}
