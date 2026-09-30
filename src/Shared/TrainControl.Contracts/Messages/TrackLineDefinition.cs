namespace TrainControl.Contracts.Messages;

/// <summary>
/// One transit line within a <see cref="TrackLayoutMessage"/>.
/// </summary>
public class TrackLineDefinition
{
    public string LineId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<TrackBlockDefinition> Blocks { get; set; } = new List<TrackBlockDefinition>();
}
