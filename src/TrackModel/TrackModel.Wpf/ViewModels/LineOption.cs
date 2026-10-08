namespace TrackModel.Wpf.ViewModels;
public sealed record LineOption(string Id)
{
    public string Name => Id + " Line";
}
