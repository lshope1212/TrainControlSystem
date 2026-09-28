using TrackController.Core.Interfaces;
using TrackController.Core.Services;

namespace TrackController.Wpf.ViewModels;

/// <summary>
/// Minimal view model demonstrating that the WPF project can reach TrackController.Core.
/// </summary>
public class MainWindowViewModel : ViewModelBase
{
    private readonly ITrackControllerService _controller;

    public MainWindowViewModel()
        : this(new TrackControllerService())
    {
    }

    public MainWindowViewModel(ITrackControllerService controller)
    {
        _controller = controller;
    }

    public string Title => "Track Controller";

    public string Status => "Architecture skeleton — no wayside logic implemented yet.";

    public string CommandedSignalState => _controller.State.CommandedSignalState.ToString();

    public string CommandedSwitchPosition => _controller.State.CommandedSwitchPosition.ToString();
}
