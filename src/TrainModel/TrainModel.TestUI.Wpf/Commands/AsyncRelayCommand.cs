using System.Windows.Input;

namespace TrainModel.TestUI.Wpf.Commands;

/// <summary>ICommand for async handlers; disables itself while running.</summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _executeAsync;
    private readonly Func<bool>? _canExecute;
    private bool _isRunning;

    public AsyncRelayCommand(Func<Task> executeAsync, Func<bool>? canExecute = null)
    {
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _isRunning = true;
        CommandManager.InvalidateRequerySuggested();
        try { await _executeAsync(); }
        finally
        {
            _isRunning = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
