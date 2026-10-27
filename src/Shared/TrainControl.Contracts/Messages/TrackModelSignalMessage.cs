using TrainControl.Contracts.Enums;

namespace TrainControl.Contracts.Messages;

/// <summary>Track Model -> Train Controller: track signal at a block.</summary>
public sealed class TrackModelSignalMessage
{
    public string BlockId { get; set; } = string.Empty;
    public string TrainId { get; set; } = string.Empty;
    public SignalState Signal { get; set; }
}
