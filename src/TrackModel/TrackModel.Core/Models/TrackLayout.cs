namespace TrackModel.Core.Models;
public class TrackLayout
{
    public string Name { get; set; } = "Untitled layout";
    public List<TrackBlock> Blocks { get; set; } = [];
}
