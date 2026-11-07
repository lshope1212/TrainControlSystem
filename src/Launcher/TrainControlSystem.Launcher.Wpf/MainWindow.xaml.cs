using System.Windows;
using TrainControlSystem.Launcher.Wpf.ViewModels;

namespace TrainControlSystem.Launcher.Wpf;

/// <summary>
/// Interaction logic for MainWindow.xaml. Code-behind is intentionally kept empty —
/// process launching lives in ModuleLauncherService, the shared clock in SimulationClockHost.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }
}
