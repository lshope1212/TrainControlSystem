using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TrainControl.Common.Timing;
using TrainControlSystem.Launcher.Wpf.Commands;
using TrainControlSystem.Launcher.Wpf.Models;
using TrainControlSystem.Launcher.Wpf.Services;

namespace TrainControlSystem.Launcher.Wpf.ViewModels;

/// <summary>
/// View model for the central launcher window. Holds the list of subsystem
/// applications and delegates all process work to <see cref="ModuleLauncherService"/>.
/// It also SHOWS the shared simulation clock hosted by this process, read-only: only the
/// CTC Office may run, pause, speed up or set the clock.
/// </summary>
public class MainWindowViewModel : ViewModelBase
{
    private readonly ModuleLauncherService _launcher;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, string> _deliveryByModule = new Dictionary<string, string>();

    private SimulationClockState _clockState;
    private string _clockDelivery = string.Empty;
    private string _clockNotice = string.Empty;
    private string _statusMessage = string.Empty;

    public MainWindowViewModel(ModuleLauncherService launcher, SimulationClockHost clockHost, Dispatcher dispatcher)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        ArgumentNullException.ThrowIfNull(clockHost);
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _launcher.ModuleExited += OnModuleExited;

        _clockState = clockHost.Current;
        foreach (var name in clockHost.SubscriberNames)
        {
            _deliveryByModule[name] = "waiting";
        }

        _clockDelivery = FormatDelivery();

        // Clock events arrive on background threads.
        clockHost.StatePublished += (_, state) => _dispatcher.BeginInvoke(() => ClockState = state);
        clockHost.DeliveryStatusChanged += (_, status) => _dispatcher.BeginInvoke(() => ApplyDelivery(status));
        clockHost.StatusReported += (_, notice) => _dispatcher.BeginInvoke(() => ClockNotice = notice);

        Modules = new ObservableCollection<ModuleInfo>
        {
            new()
            {
                Name = "Train Model",
                ExecutableName = "TrainModel.Wpf.exe",
                ProjectDirectory = "src/TrainModel/TrainModel.Wpf"
            },
            new()
            {
                Name = "Track Model",
                ExecutableName = "TrackModel.Wpf.exe",
                ProjectDirectory = "src/TrackModel/TrackModel.Wpf"
            },
            new()
            {
                Name = "Train Controller",
                ExecutableName = "TrainController.Wpf.exe",
                ProjectDirectory = "src/TrainController/TrainController.Wpf"
            },
            new()
            {
                Name = "Track Controller",
                ExecutableName = "TrackController.Wpf.exe",
                ProjectDirectory = "src/TrackController/TrackController.Wpf"
            },
            new()
            {
                Name = "CTC Office",
                ExecutableName = "CTC.Wpf.exe",
                ProjectDirectory = "src/CTC/CTC.Wpf"
            }
        };

        LaunchModuleCommand = new RelayCommand(
            execute: parameter => LaunchModule(parameter as ModuleInfo),
            canExecute: parameter => parameter is ModuleInfo { IsRunning: false });

        LaunchAllCommand = new RelayCommand(_ => LaunchAll());
    }

    public string Title => "Train Control System";

    public ObservableCollection<ModuleInfo> Modules { get; }

    public ICommand LaunchModuleCommand { get; }

    public ICommand LaunchAllCommand { get; }

    /// <summary>Authoritative state of the shared simulation clock, as last distributed.</summary>
    public SimulationClockState ClockState
    {
        get => _clockState;
        private set
        {
            if (SetProperty(ref _clockState, value))
            {
                OnPropertyChanged(nameof(SystemTimeDisplay));
                OnPropertyChanged(nameof(SimulationSpeedDisplay));
                OnPropertyChanged(nameof(ClockStatusDisplay));
            }
        }
    }

    public string SystemTimeDisplay => ClockState.SystemTime.ToString(@"hh\:mm\:ss");

    public string SimulationSpeedDisplay => $"{ClockState.SpeedMultiplier:0.#}x";

    public string ClockStatusDisplay => ClockState.IsPaused ? "Paused" : "Running";

    /// <summary>Whether each subscribed module is receiving clock updates, e.g. "CTC: receiving".</summary>
    public string ClockDelivery
    {
        get => _clockDelivery;
        private set => SetProperty(ref _clockDelivery, value);
    }

    /// <summary>Latest clock notice (e.g. the last control request CTC sent).</summary>
    public string ClockNotice
    {
        get => _clockNotice;
        private set => SetProperty(ref _clockNotice, value);
    }

    /// <summary>Last message shown to the user (launch results and failures).</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    private void LaunchModule(ModuleInfo? module)
    {
        if (module is null)
        {
            return;
        }

        StatusMessage = _launcher.LaunchModule(module, out var error)
            ? $"Started {module.Name}."
            : error ?? string.Empty;
    }

    private void LaunchAll()
    {
        var errors = _launcher.LaunchAll(Modules);

        StatusMessage = errors.Count == 0
            ? "Started all modules."
            : string.Join("  ", errors);
    }

    private void ApplyDelivery(ClockDeliveryStatus status)
    {
        _deliveryByModule[status.ModuleName] = status.Detail;
        ClockDelivery = FormatDelivery();
    }

    private string FormatDelivery() =>
        string.Join("  •  ", _deliveryByModule.Select(module => $"{module.Key}: {module.Value}"));

    private void OnModuleExited(object? sender, ModuleInfo module)
    {
        // Process.Exited fires on a background thread; marshal back to the UI thread.
        Application.Current?.Dispatcher.Invoke(() => module.IsRunning = false);
    }
}
