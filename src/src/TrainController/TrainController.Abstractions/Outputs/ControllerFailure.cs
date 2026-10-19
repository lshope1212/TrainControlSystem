namespace TrainController.Abstractions.Outputs;

/// <summary>
/// Latched failures detected by the Train Controller. All are INFERRED from normal observable
/// data (no Train Model failure flags). Each stays latched until the driver presses
/// "Reset failures" and that failure's reset condition is met.
/// </summary>
[Flags]
public enum ControllerFailure
{
    None = 0,

    /// <summary>Track signal invalid continuously for <c>SignalPickupFailureLatchSeconds</c>. Reset: signal valid again.</summary>
    SignalPickup = 1 << 0,

    /// <summary>Service brake commanded but the train does not decelerate enough. Reset: train stopped.</summary>
    Brake = 1 << 1,

    /// <summary>Significant traction commanded but the train does not accelerate. Reset: train stopped.</summary>
    Engine = 1 << 2,

    /// <summary>Hardware trains only: Raspberry Pi link fault (set by the integration layer). Reset: link healthy.</summary>
    HardwareLink = 1 << 3,
}
