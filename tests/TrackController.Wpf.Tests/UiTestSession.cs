using Pipe = TrackController.Wpf.Services.WaysidePipeHost.Pipe;
using System.Windows.Input;
using System.Windows.Threading;
using TrackController.Core.Services;
using TrackController.Wpf.Services;
using OperatorModel = TrackController.Wpf.ViewModels.MainWindowViewModel;
using TestModel = TrackController.TestUI.Wpf.ViewModels.MainWindowViewModel;

namespace TrackController.Wpf.Tests;

/// <summary>Runs both applications' view models with isolated real pipes and WPF bindings.</summary>
internal sealed class UiTestSession : IAsyncDisposable
{
    private bool _controllerStopped;

    public OperatorModel Controller { get; }

    public TestModel Inputs { get; }

    private UiTestSession()
    {
        var endpoints = WaysidePipeHost.CreatePipeNames(isolated: true);
        Controller = new OperatorModel(new TrackControllerService(), endpoints);
        Inputs = new TestModel(endpoints[(int)Pipe.Input], endpoints[(int)Pipe.Ctc], endpoints[(int)Pipe.TrackModel]);
    }

    /// <summary>Executes an actual command from either application.</summary>
    public static Task ExecuteAsync(ICommand command) => command switch
    {
        TrackController.Wpf.Commands.AsyncRelayCommand action => action.ExecuteAsync(),
        TestUI.Wpf.Commands.AsyncRelayCommand action => action.ExecuteAsync(),
        _ => throw new ArgumentException("Expected an asynchronous UI command.", nameof(command))
    };

    /// <summary>Waits for asynchronously received output.</summary>
    public static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition())
        {
            await Task.Delay(15, deadline.Token);
        }
    }

    /// <summary>Disconnects the controller to exercise observable transport failures.</summary>
    public async Task StopControllerAsync()
    {
        if (!_controllerStopped)
        {
            _controllerStopped = true;
            await Controller.DisposeAsync();
        }
    }

    /// <summary>Stops both applications' background operations.</summary>
    public async ValueTask DisposeAsync()
    {
        await Inputs.DisposeAsync();
        await StopControllerAsync();
    }

    /// <summary>Creates a WPF dispatcher and propagates integration failures.</summary>
    public static async Task RunAsync(Func<UiTestSession, Task> action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await using var session = new UiTestSession();
                    await UntilAsync(() => session.Inputs.Blocks.Count == 12);
                    await action(session);
                    completed.TrySetResult();
                }
                catch (Exception exception)
                {
                    completed.TrySetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "Track controller UI integration" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }
}
