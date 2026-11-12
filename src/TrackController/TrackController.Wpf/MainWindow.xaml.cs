using System.Windows;
using TrackController.Wpf.ViewModels;

namespace TrackController.Wpf;

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
        // WorkArea is in WPF device-independent units and excludes the taskbar.
        var work = SystemParameters.WorkArea;
        Width = Math.Min(1600, work.Width * 0.92);
        Height = Math.Min(1000, work.Height * 0.92);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
    }
}
