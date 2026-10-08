using System.Windows.Input;
namespace TrackModel.TestUI.Wpf.Commands;

public sealed class AsyncRelayCommand(Func<Task> execute) : ICommand
{
    private bool _busy;
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => !_busy;
    public async void Execute(object? parameter)
    {
        if (_busy) return;
        _busy = true; CommandManager.InvalidateRequerySuggested();
        try { await execute(); }
        finally { _busy = false; CommandManager.InvalidateRequerySuggested(); }
    }
}
