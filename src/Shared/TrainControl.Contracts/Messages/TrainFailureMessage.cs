namespace TrainControl.Contracts.Messages;

/// <summary>
/// Fault-injection state driven by the Murphy user (and reported back for display).
/// Engine failure is equivalent to power failure: the engine produces no tractive
/// force. Signal-pickup failure manifests as loss of the track signal. Brake
/// failure is included per the module spec though it is not exercised in the
/// professor's scenario.
/// </summary>
public class TrainFailureMessage
{
    public string TrainId { get; set; } = string.Empty;

    public bool EngineFailure { get; set; }

    public bool BrakeFailure { get; set; }

    public bool SignalPickupFailure { get; set; }
}
