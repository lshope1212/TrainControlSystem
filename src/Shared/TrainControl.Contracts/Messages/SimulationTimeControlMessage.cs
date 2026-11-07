using TrainControl.Contracts.Enums;

namespace TrainControl.Contracts.Messages;

/// <summary>
/// Request to change the shared simulation clock.
/// Direction: CTC -> shared simulation clock (<c>NamedPipeNames.SimulationClock</c>). CTC is
/// the only operational module allowed to send it.
/// </summary>
/// <remarks>
/// A request is not an acknowledgement: the clock answers only by publishing its new state as
/// a <see cref="SystemTimeMessage"/>. Senders must display that authoritative state, never the
/// state they asked for. An invalid request (e.g. a speed outside 1..10) leaves the clock unchanged.
/// </remarks>
public class SimulationTimeControlMessage
{
    public SimulationClockCommand Command { get; set; }

    /// <summary>Required for <see cref="SimulationClockCommand.SetSpeed"/>; ignored otherwise.</summary>
    public double? SpeedMultiplier { get; set; }

    /// <summary>Required for <see cref="SimulationClockCommand.SetTime"/>; ignored otherwise.</summary>
    public TimeSpan? SystemTime { get; set; }
}
