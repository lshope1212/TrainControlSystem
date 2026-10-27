using System.Windows;
using TrackModel.TestUI.Wpf.Services;
using TrackModel.TestUI.Wpf.ViewModels;
namespace TrackModel.TestUI.Wpf;

public partial class App : Application
{
    private readonly CancellationTokenSource _shutdown = new();
    private MainWindowViewModel? _vm;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var connection = new ExternalModuleSimulator(Dispatcher);
        _vm = new MainWindowViewModel(connection);
        MainWindow = new MainWindow(_vm);
        MainWindow.Show();
        _ = connection.RunAsync(_shutdown.Token);
        _ = _vm.RefreshAsync();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _vm?.Stop();
        _shutdown.Cancel();
        base.OnExit(e);
    }
}
