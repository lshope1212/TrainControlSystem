using System.Windows;
using TrainControlSystem.Launcher.Wpf.Services;
using TrainControlSystem.Launcher.Wpf.ViewModels;

namespace TrainControlSystem.Launcher.Wpf;

/// <summary>
/// Composition root of the Launcher process. Besides starting modules, the Launcher hosts the
/// system's single shared simulation clock for as long as it runs (see <see cref="SimulationClockHost"/>).
/// </summary>
public partial class App : Application
{
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var clockHost = new SimulationClockHost();
        var viewModel = new MainWindowViewModel(new ModuleLauncherService(), clockHost, Dispatcher);
        var window = new MainWindow(viewModel);

        // Subscribed before starting, so the first published state is shown. Runs until OnExit.
        _ = clockHost.RunAsync(_shutdown.Token);

        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();

        base.OnExit(e);
    }
}
