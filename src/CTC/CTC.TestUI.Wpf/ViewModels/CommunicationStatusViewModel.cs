using CTC.TestUI.Wpf.Services;
using TrainControl.Common.Communication;

namespace CTC.TestUI.Wpf.ViewModels;

/// <summary>
/// State of the two process boundaries the TestUI uses: sending into CTC, and listening
/// as the fake Track Controller.
/// </summary>
public class CommunicationStatusViewModel : ViewModelBase
{
    private string _ctcEndpointStatus = "Nothing sent yet.";
    private string _trackControllerListenerStatus;

    public CommunicationStatusViewModel(FakeTrackControllerReceiver receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);

        _trackControllerListenerStatus = $"Listening on {receiver.PipeName}.";
        receiver.MessageReceived += (_, envelope) =>
            TrackControllerListenerStatus = $"Listening on {receiver.PipeName}. Last received: {envelope.MessageType} at {DateTime.Now:HH:mm:ss}.";
        receiver.ErrorOccurred += (_, error) =>
            TrackControllerListenerStatus = $"Listening on {receiver.PipeName}. Last error at {DateTime.Now:HH:mm:ss}: {error}";
    }

    public string CtcPipeName => NamedPipeNames.Ctc;

    /// <summary>Outcome of the most recent send to CTC.</summary>
    public string CtcEndpointStatus
    {
        get => _ctcEndpointStatus;
        private set => SetProperty(ref _ctcEndpointStatus, value);
    }

    public string TrackControllerListenerStatus
    {
        get => _trackControllerListenerStatus;
        private set => SetProperty(ref _trackControllerListenerStatus, value);
    }

    public void ReportCtcSend(string result) =>
        CtcEndpointStatus = $"{DateTime.Now:HH:mm:ss} {result}";
}
