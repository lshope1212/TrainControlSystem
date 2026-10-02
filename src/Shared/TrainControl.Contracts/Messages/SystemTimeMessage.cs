namespace TrainControl.Contracts.Messages;

/// <summary>
/// Current simulation time of day from the shared simulation clock.
/// Direction: shared system clock -> every module (currently consumed by CTC).
/// </summary>
public class SystemTimeMessage
{
    public TimeSpan SystemTime { get; set; }
}
