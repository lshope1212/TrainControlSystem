namespace TrainControl.Contracts.Messages;

/// <summary>Block-based train placement and a passenger exchange at its current block, used by the
/// development harness (tools/Send-TrackModelTrainUpdate.ps1) and the TestUIs.
/// An empty CurrentBlockId removes the train from the track. ExchangeId makes station
/// exchanges idempotent when a sender retries the same operation.</summary>
/// <remarks>
/// Not the Train Model's telemetry: a dispatched train is placed by <see cref="TrainDispatchRequestMessage"/>
/// and tracked from <see cref="TrainStateMessage"/> (cumulative position, length), which can span several
/// blocks. The Track Model refuses a block move for such a train; an update naming its head block applies
/// only the passenger exchange, and an empty CurrentBlockId retires it.
/// </remarks>
public sealed class TrackModelTrainUpdateMessage
{
    public string TrainId { get; set; } = string.Empty;
    public string CurrentBlockId { get; set; } = string.Empty;
    public double ActualSpeedMetersPerSecond { get; set; }
    public int BoardingPassengers { get; set; }
    public int DisembarkingPassengers { get; set; }
    public string ExchangeId { get; set; } = string.Empty;
}
