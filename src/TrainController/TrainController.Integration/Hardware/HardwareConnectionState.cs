namespace TrainController.Integration.Hardware;

/// <summary>
/// Health of the single Windows <-> Raspberry Pi connection shared by all five Hardware
/// trains. Per-train controller faults are reported separately on each train's output.
/// </summary>
public enum HardwareConnectionState
{
    Disconnected = 0,
    Connecting,

    /// <summary>Connected and handshaken, but no controller requests are currently flowing.</summary>
    Ready,

    /// <summary>Connected and actively exchanging controller requests/responses.</summary>
    Active,

    /// <summary>Connection failed or a response was invalid; affected trains are in fail-safe.</summary>
    Faulted
}
