using System.Windows.Input;

namespace TrackController.Wpf.Commands;

/// <summary>Awaitable commands keep pipe I/O off the UI thread and prevent double submission.</summary>
public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;
    /// <summary>Reports whether the command can currently run so the UI can disable unavailable actions.</summary>
    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }

    /// <summary>Runs the bound asynchronous operator action through the WPF command interface.</summary>
    public async void Execute(object? parameter) => await ExecuteAsync();
    /// <summary>Runs the command once at a time and restores its availability on completion.</summary>
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
            return;
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await execute();
        }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
