using TrainModel.Core.Interfaces;
using TrainModel.Core.Services;

namespace TrainModel.Wpf.ViewModels;

/// <summary>
/// Minimal view model demonstrating that the WPF project can reach TrainModel.Core.
/// No simulation or business logic belongs here.
/// </summary>
public class MainWindowViewModel : ViewModelBase
{
    private readonly ITrainSimulationService _simulation;

    public MainWindowViewModel()
        : this(new TrainSimulationService())
    {
    }

    public MainWindowViewModel(ITrainSimulationService simulation)
    {
        _simulation = simulation;
    }

    public string Title => "Train Model";

    public string Status => "Architecture skeleton — no simulation implemented yet.";

    public string TrainId => _simulation.Train.Id;

    public double PositionMeters => _simulation.Train.PositionMeters;

    public double VelocityMetersPerSecond => _simulation.Train.VelocityMetersPerSecond;
}
