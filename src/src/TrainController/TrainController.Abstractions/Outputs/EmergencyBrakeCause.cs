namespace TrainController.Abstractions.Outputs;

/// <summary>
/// Active emergency-brake conditions. Several may be active at once; the emergency brake
/// stays applied until ALL of them are cleared.
/// </summary>
/// <remarks>
/// How each cause is cleared: <see cref="Driver"/> / <see cref="Passenger"/> → E-brake reset
/// (train stopped, no passenger request active); <see cref="BrakeFailure"/> and
/// <see cref="HardwareCommunication"/> → "Reset failures"; <see cref="AuthorityViolation"/> →
/// released automatically once the train has stopped.
/// </remarks>
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

    /// <summary>
    /// Train cannot stop within the remaining authority (including: no trusted authority while the
    /// track signal is invalid). Protection action, not a failure: released once stopped.
    /// </summary>
    AuthorityViolation = 1 << 3,

    /// <summary>Controller received an input that failed validation.</summary>
    InvalidInput = 1 << 4,

    /// <summary>Internal controller fault (exception, invalid output, ...).</summary>
    ControllerFault = 1 << 5,

    // 1 << 6 was "TrackSignalLoss" (removed: signal loss always uses the service brake).

    /// <summary>Brake failure detected: the service brake is ineffective, so the emergency brake is used.</summary>
    BrakeFailure = 1 << 7,
}
