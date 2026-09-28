using TrainController.Core.Interfaces;
using TrainController.Core.Services;

namespace TrainController.Wpf.ViewModels;

/// <summary>
/// Minimal view model demonstrating that the WPF project can reach TrainController.Core.
/// </summary>
public class MainWindowViewModel : ViewModelBase
{
    private readonly ITrainControllerService _controller;

    public MainWindowViewModel()
        : this(new TrainControllerService())
    {
    }

    public MainWindowViewModel(ITrainControllerService controller)
    {
        _controller = controller;
    }

    public string Title => "Train Controller";

    public string Status => "Architecture skeleton — no control logic implemented yet.";

    public double CommandedSpeedMetersPerSecond => _controller.State.CommandedSpeedMetersPerSecond;

    public double AuthorityMeters => _controller.State.AuthorityMeters;
}
