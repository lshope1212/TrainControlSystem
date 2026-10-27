using System.ComponentModel;
using System.Windows;
using TrackController.Core.Services;
using TrackController.Wpf.ViewModels;

namespace TrackController.Wpf;

/// <summary>
/// Creates the controller instance and closes its connections before the UI exits.
/// </summary>
public partial class App : Application
{
    private MainWindowViewModel? _viewModel;
    private bool _closing;

    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        _viewModel = new MainWindowViewModel(new TrackControllerService());
        var window = new MainWindow(_viewModel);
        window.Closing += OnWindowClosing;
        MainWindow = window;
        window.Show();
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs eventArgs)
    {
        eventArgs.Cancel = true;
        if (_closing || sender is not Window window || _viewModel is null)
        {
            return;
        }

        _closing = true;
        try
        {
            await _viewModel.DisposeAsync();
        }
        finally
        {
            window.Closing -= OnWindowClosing;
            window.Close();
        }
    }
}
