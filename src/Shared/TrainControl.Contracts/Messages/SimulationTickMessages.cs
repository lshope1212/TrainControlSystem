namespace TrainControl.Contracts.Messages;

/// <summary>
/// Global simulation tick (system timing design). Direction: clock host → every participant.
/// Tick N covers simulated time (N − 1)·dt … N·dt; dt is always 0.1 s.
/// </summary>
public class TickMessage
{
    public long TickId { get; set; }

    /// <summary>Simulation time at the END of this tick (TickId × dt).</summary>
    public double SimulationTimeSeconds { get; set; }

    public double DeltaTimeSeconds { get; set; }
}

/// <summary>
/// A participant has finished tick <see cref="TickId"/>. Direction: participant → clock host.
/// The host starts the next tick only after every participant has reported done.
/// </summary>
public class TickDoneMessage
{
    public long TickId { get; set; }

    /// <summary>The participant's inbound pipe name (e.g. "TrainControl.TrainModel").</summary>
    public string Participant { get; set; } = string.Empty;
}

/// <summary>Clock control request kinds.</summary>
public enum ClockCommand
{
    Run = 0,
    Pause = 1,
    Step = 2,
    SetSpeed = 3,
    Reset = 4,
}

/// <summary>
/// A request to control the global clock. Direction: any participant → clock host. The host
/// applies it at the next tick boundary and broadcasts the new <see cref="ClockStateMessage"/>.
/// </summary>
public class ClockControlMessage
{
    public ClockCommand Command { get; set; }

    /// <summary>Only for <see cref="ClockCommand.SetSpeed"/>: 1 … 10.</summary>
    public double SpeedMultiplier { get; set; } = 1.0;

    /// <summary>Who asked (pipe name), for the log.</summary>
    public string Requester { get; set; } = string.Empty;
}

/// <summary>Clock state, broadcast on every change. Direction: clock host → every participant.</summary>
public class ClockStateMessage
{
    public long TickId { get; set; }

    public double SimulationTimeSeconds { get; set; }

    public bool IsRunning { get; set; }

    public double SpeedMultiplier { get; set; } = 1.0;

    /// <summary>Empty when healthy; otherwise why the clock stopped (e.g. a participant timed out).</summary>
    public string Fault { get; set; } = string.Empty;
}

/// <summary>
/// Global reset. Direction: clock host → every participant. Each participant clears its
/// runtime state and buffered messages, then replies <see cref="ResetDoneMessage"/>.
/// The host restarts from tick 1 only after every participant has replied.
/// </summary>
public class ResetMessage
{
    public long ResetId { get; set; }
}

/// <summary>Direction: participant → clock host.</summary>
public class ResetDoneMessage
{
    public long ResetId { get; set; }

    public string Participant { get; set; } = string.Empty;
}
