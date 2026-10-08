using System.Windows.Input;

namespace TrackController.TestUI.Wpf.Commands;

/// <summary>
/// Standard lightweight ICommand implementation for future use.
/// Intentionally duplicated in each WPF project rather than shared.
/// </summary>
public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    /// <summary>Connects a UI action and its availability predicate to the WPF command interface.</summary>
    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    /// <summary>Reports whether the command can currently run so the UI can disable unavailable actions.</summary>
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <summary>Invokes the bound synchronous operator action.</summary>
    public void Execute(object? parameter) => _execute(parameter);
}
