using System.Windows;
using CTC.TestUI.Wpf.Services;
using CTC.TestUI.Wpf.ViewModels;

namespace CTC.TestUI.Wpf;

/// <summary>
/// Composition root of the TestUI process: a sender into CTC's pipe and a fake Track
/// Controller listener. It never hosts CTC itself.
/// </summary>
public partial class App : Application
{
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ICtcMessageSender sender = new NamedPipeCtcMessageSender();
        var receiver = new FakeTrackControllerReceiver(Dispatcher);

        var viewModel = new MainWindowViewModel(sender, receiver, _shutdown.Token);
        var window = new MainWindow(viewModel);

        // Runs until OnExit cancels it; the receive loop never throws for bad messages.
        _ = receiver.RunAsync(_shutdown.Token);

        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Stops the receive loop, the simulated clock's timer and any in-progress send.
        // Not disposed: the receive loop may still be observing the token as it unwinds.
        _shutdown.Cancel();

        base.OnExit(e);
    }
}
