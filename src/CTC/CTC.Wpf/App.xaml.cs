using System.Windows;
using CTC.Core.Interfaces;
using CTC.Core.Services;
using CTC.Wpf.Services;
using CTC.Wpf.ViewModels;

namespace CTC.Wpf;

/// <summary>
/// Composition root of the CTC process. Builds exactly ONE <see cref="CTCService"/> and
/// shares it between the dispatcher UI and the inbound named-pipe receiver. CTC controls the
/// shared simulation clock (hosted by the Launcher) but has no clock of its own: the
/// dispatcher's clock controls send requests, and the clock's state comes back in through
/// the receiver like any other inbound message.
/// </summary>
public partial class App : Application
{
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        IMessageSender sender = new NamedPipeMessageSender();
        ICTCService ctcService = new CTCService(sender);

        var clock = new SimulationClockViewModel(new NamedPipeSimulationClockControlSender());
        var viewModel = new MainWindowViewModel(ctcService, clock);
        var window = new MainWindow(viewModel);

        var receiver = new CtcNamedPipeReceiver(ctcService, Dispatcher);
        receiver.StatusReported += (_, status) => viewModel.InboundStatus = status;
        receiver.SystemTimeReceived += (_, state) => clock.ApplyClockState(state);

        // Fetches the production layout if Track Model started first, expires stale wayside
        // statuses and renews movement requests, on wall time.
        var links = new CtcLinkSupervisor(ctcService, Dispatcher);
        links.StatusReported += (_, status) => viewModel.InboundStatus = status;

        // Both run until OnExit cancels them; neither loop throws for bad messages or offline peers.
        _ = receiver.RunAsync(_shutdown.Token);
        _ = links.RunAsync(_shutdown.Token);

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
