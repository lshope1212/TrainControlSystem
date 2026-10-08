using System.Windows;
using TrainController.Wpf.ViewModels;

namespace TrainController.Wpf;

/// <summary>
/// Test UI window (Train Model stand-in). Peer of <see cref="MainWindow"/>, not its child.
/// </summary>
public partial class TestWindow : Window
{
    public TestWindow(TestWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Loaded += (_, _) => viewModel.StartLiveUpdates();
        Closed += (_, _) => viewModel.StopLiveUpdates();
    }
}
