namespace TrainControl.Contracts.Messages;

/// <summary>Sets the ambient temperature of one track block, in SI units.</summary>
public sealed class TrackModelTemperatureCommandMessage
{
    public string BlockId { get; set; } = string.Empty;
    public double TemperatureCelsius { get; set; }
}
