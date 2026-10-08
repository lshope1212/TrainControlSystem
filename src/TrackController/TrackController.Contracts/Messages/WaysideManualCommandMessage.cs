using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;

namespace TrackController.Contracts.Messages;

/// <summary>Requests maintenance equipment commands that remain subject to protection checks.</summary>
public sealed class WaysideManualCommandMessage
{
    public string BlockId { get; set; } = "";

    public SwitchPosition? Switch { get; set; }

    public SignalState? Signal { get; set; }

    public CrossingState? Crossing { get; set; }
}
