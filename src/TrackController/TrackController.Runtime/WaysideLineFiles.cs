namespace TrackController.Runtime;

/// <summary>One line of a multi-line deployment: its shared track map and the territories that own its blocks.</summary>
public sealed class WaysideLineFiles
{
    public string? TrackFile { get; set; }

    public string? TerritoriesFile { get; set; }
}
