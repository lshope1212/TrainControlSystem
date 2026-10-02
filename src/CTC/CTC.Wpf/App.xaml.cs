using System.Windows;
using CTC.Core.Interfaces;
using CTC.Core.Services;
using CTC.Wpf.Services;
using CTC.Wpf.ViewModels;

namespace CTC.Wpf;

/// <summary>
/// Composition root of the CTC process. Builds exactly ONE <see cref="CTCService"/> and
/// shares it between the dispatcher UI and the inbound named-pipe receiver.
/// </summary>
public partial class App : Application
{
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        IMessageSender sender = new NamedPipeMessageSender();
        ICTCService ctcService = new CTCService(sender);

        var viewModel = new MainWindowViewModel(ctcService);
        var window = new MainWindow(viewModel);

        var receiver = new CtcNamedPipeReceiver(ctcService, Dispatcher);
        receiver.StatusReported += (_, status) => viewModel.InboundStatus = status;

        // Runs until OnExit cancels it; the receive loop never throws for bad messages.
        _ = receiver.RunAsync(_shutdown.Token);

        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Not disposed: the receive loop may still be observing the token as it unwinds.
        _shutdown.Cancel();

        base.OnExit(e);
    }
}
