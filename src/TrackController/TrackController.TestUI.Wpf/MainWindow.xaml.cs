using System.Windows;
using TrackController.TestUI.Wpf.ViewModels;

namespace TrackController.TestUI.Wpf;

/// <summary>
/// Binds the window to the view model supplied by the application.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Initializes the view and binds the application-supplied view model.</summary>
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
