using TrainControl.Contracts.Enums;

namespace TrainControl.Contracts.Messages;

/// <summary>Wayside commands for one track block. All physical quantities use SI units.</summary>
public sealed class TrackModelCommandMessage
{
    public string BlockId { get; set; } = string.Empty;
    public double CommandedSpeedMetersPerSecond { get; set; }
    public double AuthorityMeters { get; set; }
    public SwitchPosition Switch { get; set; } = SwitchPosition.Normal;
    public SignalState Signal { get; set; } = SignalState.Green;
    public CrossingState Crossing { get; set; } = CrossingState.Open;
}
