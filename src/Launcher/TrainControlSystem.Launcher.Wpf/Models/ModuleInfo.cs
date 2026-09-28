using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TrainControlSystem.Launcher.Wpf.Models;

/// <summary>
/// Describes one independently runnable subsystem application that the launcher
/// can start as a separate operating-system process.
/// </summary>
/// <remarks>
/// This is deliberately a description of an <em>executable</em>, not of a subsystem's
/// domain types. The launcher holds no project reference to the subsystem WPF
/// applications — it only needs to know where their executables land on disk.
/// </remarks>
public class ModuleInfo : INotifyPropertyChanged
{
    private bool _isRunning;

    /// <summary>Display name shown in the launcher UI, e.g. "Train Model".</summary>
    public required string Name { get; init; }

    /// <summary>File name of the built executable, e.g. "TrainModel.Wpf.exe".</summary>
    public required string ExecutableName { get; init; }

    /// <summary>
    /// Project directory relative to the repository root, e.g.
    /// "src/TrainModel/TrainModel.Wpf". Used to locate the module's build output.
    /// </summary>
    public required string ProjectDirectory { get; init; }

    /// <summary>True while a process started by this launcher is still alive.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value)
            {
                return;
            }

            _isRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Status));
        }
    }

    /// <summary>Human-readable status for the UI.</summary>
    public string Status => IsRunning ? "Running" : "Stopped";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
