using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using TrainControlSystem.Launcher.Wpf.Commands;
using TrainControlSystem.Launcher.Wpf.Models;
using TrainControlSystem.Launcher.Wpf.Services;

namespace TrainControlSystem.Launcher.Wpf.ViewModels;

/// <summary>
/// View model for the central launcher window. Holds the list of subsystem
/// applications and delegates all process work to <see cref="ModuleLauncherService"/>.
/// </summary>
public class MainWindowViewModel : ViewModelBase
{
    private readonly ModuleLauncherService _launcher;

    private double _simulationSpeed = 1.0;
    private TimeSpan _systemTime = TimeSpan.Zero;
    private bool _isPaused;
    private string _statusMessage = string.Empty;

    public MainWindowViewModel()
        : this(new ModuleLauncherService())
    {
    }

    public MainWindowViewModel(ModuleLauncherService launcher)
    {
        _launcher = launcher;
        _launcher.ModuleExited += OnModuleExited;

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
        PauseCommand = new RelayCommand(_ => Pause(), _ => !IsPaused);
        ResumeCommand = new RelayCommand(_ => Resume(), _ => IsPaused);
    }

    public string Title => "Train Control System";

    public ObservableCollection<ModuleInfo> Modules { get; }

    public ICommand LaunchModuleCommand { get; }

    public ICommand LaunchAllCommand { get; }

    public ICommand PauseCommand { get; }

    public ICommand ResumeCommand { get; }

    /// <summary>
    /// FUTURE INTEGRATION POINT — shared simulation clock.
    /// Currently a static placeholder; nothing advances this value. When a shared
    /// simulation timing mechanism is chosen, this is where the launcher will
    /// surface the system-wide clock.
    /// </summary>
    public TimeSpan SystemTime
    {
        get => _systemTime;
        set => SetProperty(ref _systemTime, value);
    }

    /// <summary>
    /// FUTURE INTEGRATION POINT — shared simulation speed multiplier.
    /// Changing this currently affects nothing; the subsystem processes are not yet
    /// synchronized with the launcher.
    /// </summary>
    public double SimulationSpeed
    {
        get => _simulationSpeed;
        set => SetProperty(ref _simulationSpeed, value);
    }

    /// <summary>
    /// FUTURE INTEGRATION POINT — pause/resume state of the shared simulation.
    /// </summary>
    public bool IsPaused
    {
        get => _isPaused;
        private set => SetProperty(ref _isPaused, value);
    }

    /// <summary>Last message shown to the user (launch failures, placeholder notices).</summary>
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

    /// <summary>
    /// FUTURE INTEGRATION POINT — will eventually pause the shared simulation clock
    /// across every running subsystem process. Today it only flips a local flag.
    /// </summary>
    private void Pause()
    {
        IsPaused = true;
        StatusMessage = "Pause is a placeholder — shared simulation timing is not implemented yet.";
    }

    /// <summary>
    /// FUTURE INTEGRATION POINT — will eventually resume the shared simulation clock
    /// across every running subsystem process. Today it only flips a local flag.
    /// </summary>
    private void Resume()
    {
        IsPaused = false;
        StatusMessage = "Resume is a placeholder — shared simulation timing is not implemented yet.";
    }

    private void OnModuleExited(object? sender, ModuleInfo module)
    {
        // Process.Exited fires on a background thread; marshal back to the UI thread.
        Application.Current?.Dispatcher.Invoke(() => module.IsRunning = false);
    }
}
