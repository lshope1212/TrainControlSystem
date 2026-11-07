namespace TrainControl.Contracts.Messages;

/// <summary>
/// Authoritative simulation-clock state, distributed system-wide.
/// Direction: shared simulation clock (hosted by the Launcher) -> every subscribed module
/// (currently CTC and Train Model). During isolated testing a module's TestUI may send it
/// instead, standing in for the shared clock.
/// </summary>
/// <remarks>
/// This is a STATE notification only: receiving modules adopt it and never send it back or
/// change it. Requests to change the clock are a separate contract,
/// <see cref="SimulationTimeControlMessage"/>, which only CTC sends.
/// The defaults describe a clock running at real time, so a sender that sets only
/// <see cref="SystemTime"/> (e.g. an older TestUI) is still read sensibly.
/// </remarks>
public class SystemTimeMessage
{
    /// <summary>Current simulation time of day.</summary>
    public TimeSpan SystemTime { get; set; }

    /// <summary>Playback rate of the clock: 1 = real time, up to 10 = ten times faster.</summary>
    public double SpeedMultiplier { get; set; } = 1.0;

    /// <summary>True while the clock is paused, i.e. <see cref="SystemTime"/> is not advancing.</summary>
    public bool IsPaused { get; set; }

    /// <summary>
    /// Clock state version: increases whenever the clock's state changes. Informational
    /// (diagnostics, detecting a stale or repeated state); 0 when sent by a TestUI.
    /// </summary>
    public long Sequence { get; set; }
}
