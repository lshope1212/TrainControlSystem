using System.Windows;
using TrainModel.TestUI.Wpf.Services;
using TrainModel.TestUI.Wpf.ViewModels;
using TrainControl.Common.Communication;

namespace TrainModel.TestUI.Wpf;

/// <summary>
/// Composition root of the Train Model TestUI process. Builds the view model and
/// starts fake-neighbour receivers on the Train Controller and Track Model pipes so
/// the harness can display whatever the real Train Model sends out.
/// </summary>
public partial class App : Application
{
    private readonly CancellationTokenSource _shutdown = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var viewModel = new MainWindowViewModel(Dispatcher);
        var window = new MainWindow(viewModel);

        // The Train Model sends its outputs to the Train Controller and Track Model.
        // Stand in for both so we can capture those outputs. (Do not run alongside
        // the real neighbour processes — one owner per pipe.)
        var trainControllerListener = new FakeNeighbourReceiver(NamedPipeNames.TrainController, Dispatcher);
        var trackControllerListener = new FakeNeighbourReceiver(NamedPipeNames.TrackController, Dispatcher);

        // Messages the Train Model sends to the Train Controller vs. the Track Model
        // are captured on their respective pipes and routed to destination-specific
        // live-output sections (plus the shared capture log).
        trainControllerListener.MessageReceived += (_, env) => viewModel.RecordControllerBound(env);
        trackControllerListener.MessageReceived += (_, env) => viewModel.RecordTrackBound(env);

        trainControllerListener.ErrorOccurred += (_, msg) =>
            viewModel.Communication.NeighbourListenerStatus = $"Train Controller listener error: {msg}";
        trackControllerListener.ErrorOccurred += (_, msg) =>
            viewModel.Communication.NeighbourListenerStatus = $"Track Model listener error: {msg}";

        _ = trainControllerListener.RunAsync(_shutdown.Token);
        _ = trackControllerListener.RunAsync(_shutdown.Token);

        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();
        base.OnExit(e);
    }
}
