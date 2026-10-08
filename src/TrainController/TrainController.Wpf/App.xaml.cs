using System.IO;
using System.Windows;
using TrainController.Integration;
using TrainController.Integration.Hardware;
using TrainController.Integration.Logging;
using TrainController.Wpf.ViewModels;

namespace TrainController.Wpf;

/// <summary>
/// Composition root of the Train Controller process. Builds exactly ONE
/// <see cref="TrainControllerSubsystem"/> and opens the two PEER windows on it:
/// the Main UI (Driver / Engineer) and the Test UI (Train Model stand-in).
/// Neither window owns the other; the application exits when both are closed.
/// </summary>
public partial class App : Application
{
    private TrainControllerSubsystem? _subsystem;
    private TcpHardwareTrainControllerBackend? _hardware;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var log = new InMemoryTrainControllerEventLog();

        // appsettings.json (HardwareLink) gives the Raspberry Pi endpoint; optional per-run
        // overrides: --pi-host <host> --pi-port <port> (e.g. --pi-host 127.0.0.1 for local testing).
        HardwareLinkOptions linkOptions;
        try
        {
            linkOptions = HardwareLinkOptions
                .Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
                .WithCommandLineOverrides(e.Args);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message + "\n\nUsing default Hardware link settings.", "Train Controller", MessageBoxButton.OK, MessageBoxImage.Warning);
            linkOptions = HardwareLinkOptions.Default;
        }

        log.Log(TrainControllerLogLevel.Info, "Hardware", null, $"Raspberry Pi endpoint: {linkOptions.Endpoint}");

        _hardware = new TcpHardwareTrainControllerBackend(linkOptions, log);
        _subsystem = TrainControllerSubsystem.Create(hardwareBackend: _hardware, log: log);

        var mainWindow = new MainWindow(new MainWindowViewModel(_subsystem));
        var testWindow = new TestWindow(new TestWindowViewModel(_subsystem));

        MainWindow = mainWindow;
        mainWindow.Show();
        testWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // The process is exiting: request shutdown of the simulation loop and the Pi link
        // without blocking the UI thread on them.
        _ = _subsystem?.Simulation.StopAsync();
        _ = _hardware?.DisposeAsync().AsTask();

        base.OnExit(e);
    }
}
