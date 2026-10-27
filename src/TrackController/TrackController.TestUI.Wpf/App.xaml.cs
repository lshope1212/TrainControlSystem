using System.ComponentModel;
using System.Windows;
using TrackController.TestUI.Wpf.ViewModels;

namespace TrackController.TestUI.Wpf;

/// <summary>
/// Starts the external-module simulator and closes its pipe listeners before the UI exits.
/// </summary>
public partial class App : Application
{
    private MainWindowViewModel? _viewModel;
    private bool _closing;

    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        _viewModel = new MainWindowViewModel();
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
