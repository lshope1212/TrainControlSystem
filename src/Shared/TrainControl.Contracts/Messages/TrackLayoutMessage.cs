namespace TrainControl.Contracts.Messages;

/// <summary>
/// Transport-neutral description of the available lines and their topology.
/// Direction: Track Model -> CTC.
/// </summary>
public class TrackLayoutMessage
{
    public List<TrackLineDefinition> Lines { get; set; } = new List<TrackLineDefinition>();
}
