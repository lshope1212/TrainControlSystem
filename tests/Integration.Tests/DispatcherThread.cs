using System.Windows.Threading;

namespace TrainControl.Integration.Tests;

/// <summary>
/// A dedicated STA thread running a WPF dispatcher: stands in for one module's UI thread, which is
/// where the production components apply every state change.
/// </summary>
internal sealed class DispatcherThread : IDisposable
{
    private readonly Thread _thread;

    public DispatcherThread(string name)
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = name,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Dispatcher = ready.Task.GetAwaiter().GetResult();
    }

    public Dispatcher Dispatcher { get; }

    /// <summary>Reads or changes module state on its own thread.</summary>
    public Task<T> GetAsync<T>(Func<T> read) => Dispatcher.InvokeAsync(read).Task;

    public Task DoAsync(Action action) => Dispatcher.InvokeAsync(action).Task;

    /// <summary>Starts an async operation on the module's thread, so it resumes there (like a UI event handler).</summary>
    public Task StartAsync(Func<Task> operation) => Dispatcher.InvokeAsync(operation).Task.Unwrap();

    public void Dispose()
    {
        Dispatcher.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
