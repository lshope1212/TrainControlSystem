namespace TrackModel.Core.Models;
public class TrackLayout
{
    public string Name { get; set; } = string.Empty;
    public List<TrackBlock> Blocks { get; set; } = [];
}
