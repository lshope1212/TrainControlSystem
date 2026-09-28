using System.Windows;
using TrainControlSystem.Launcher.Wpf.ViewModels;

namespace TrainControlSystem.Launcher.Wpf;

/// <summary>
/// Interaction logic for MainWindow.xaml. Code-behind is intentionally kept empty —
/// all process-launching logic lives in ModuleLauncherService.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }
}
