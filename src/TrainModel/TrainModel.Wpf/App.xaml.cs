using System.Windows;
using TrainModel.Wpf.Services;
using TrainModel.Wpf.ViewModels;

namespace TrainModel.Wpf;

/// <summary>
/// Composition root of the Train Model process. Builds the fleet-backed view model
/// and window, then starts the inbound named-pipe receiver so neighbour modules
/// (or TrainModel.TestUI.Wpf) can drive the simulation over the TrainModel pipe.
/// </summary>
public partial class App : Application
{
    private readonly CancellationTokenSource _shutdown = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var viewModel = new MainWindowViewModel();
        var window = new MainWindow(viewModel);

        var receiver = new TrainModelNamedPipeReceiver(viewModel, Dispatcher);
        // Surface receive/routing status into the event log for visibility.
        receiver.StatusReported += (_, msg) => viewModel.ReportPipeStatus(msg);
        _ = receiver.RunAsync(_shutdown.Token);

        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();
        base.OnExit(e);
    }
}
