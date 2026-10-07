using System.Windows;
using TrainController.Wpf.ViewModels;

namespace TrainController.Wpf;

/// <summary>
/// Main UI window (Driver / Engineer). Peer of <see cref="TestWindow"/>. Code-behind only
/// wires the view model's live refresh to the window lifetime.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Loaded += (_, _) => viewModel.StartLiveUpdates();
        Closed += (_, _) => viewModel.StopLiveUpdates();
    }
}
