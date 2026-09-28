namespace TrackModel.Core.Models;

/// <summary>
/// Placeholder container for the blocks that make up a track layout.
/// </summary>
public class TrackLayout
{
    public string Name { get; set; } = string.Empty;

    public IList<TrackBlock> Blocks { get; } = new List<TrackBlock>();
}
