using TrackModel.Core.Interfaces;
using TrackModel.Core.Services;

namespace TrackModel.Wpf.ViewModels;

/// <summary>
/// Minimal view model demonstrating that the WPF project can reach TrackModel.Core.
/// </summary>
public class MainWindowViewModel : ViewModelBase
{
    private readonly ITrackService _trackService;

    public MainWindowViewModel()
        : this(new TrackService())
    {
    }

    public MainWindowViewModel(ITrackService trackService)
    {
        _trackService = trackService;
    }

    public string Title => "Track Model";

    public string Status => "Architecture skeleton — no track layout loaded yet.";

    public string LayoutName => _trackService.Layout.Name;

    public int BlockCount => _trackService.Layout.Blocks.Count;
}
