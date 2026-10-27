namespace TrainController.Integration.Hardware;

/// <summary>
/// Health of the single Windows -> Raspberry Pi link, exposed by the Hardware backend so the
/// Main UI can show it. Per-train controller faults are reported on each train's output.
/// </summary>
public interface IHardwareConnectionStatus
{
    HardwareConnectionState ConnectionState { get; }

    /// <summary>Short human-readable detail (endpoint, last error, ...).</summary>
    string ConnectionDetail { get; }
}
