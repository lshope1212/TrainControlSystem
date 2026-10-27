using System.Windows;
using TrackModel.Core.Services;
using TrackModel.Wpf.Services;
using TrackModel.Wpf.ViewModels;

namespace TrackModel.Wpf;
public partial class App : Application
{
    private readonly CancellationTokenSource _shutdown = new();
    private TrackModelConnection? _connection;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var track = new TrackService();
        BlueLineTrackLayout.LoadDemonstration(track);
        var vm = new MainWindowViewModel(track);
        _connection = new TrackModelConnection(track, Dispatcher);
        _connection.StatusReported += (_, status) => vm.Status = status;
        _connection.DeliveryReported += (_, status) => vm.Delivery = status;
        MainWindow = new MainWindow(vm);
        MainWindow.Show();
        _ = _connection.RunAsync(_shutdown.Token);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _connection?.Stop();
        _shutdown.Cancel();
        base.OnExit(e);
    }
}
