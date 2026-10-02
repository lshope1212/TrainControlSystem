using System.Collections.ObjectModel;
using System.Windows.Input;
using CTC.TestUI.Wpf.Commands;
using CTC.TestUI.Wpf.Models;
using CTC.TestUI.Wpf.Services;
using TrainControl.Common.Communication;

namespace CTC.TestUI.Wpf.ViewModels;

/// <summary>
/// Lists every message the running CTC actually delivered to the fake Track Controller
/// endpoint (<see cref="FakeTrackControllerReceiver"/>), plus any receive errors.
/// </summary>
public class CapturedOutputsViewModel : ViewModelBase
{
    private int _nextSequenceNumber = 1;

    public CapturedOutputsViewModel(FakeTrackControllerReceiver receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);

        // Listen for messages and errors from the fake Track Controller.
        receiver.MessageReceived += OnMessageReceived;
        receiver.ErrorOccurred += OnErrorOccurred;

        // Command used by the UI to clear captured messages.
        ClearCommand = new RelayCommand(_ => Messages.Clear(), _ => Messages.Count > 0);
    }

    /// <summary>
    /// All messages/errors captured by the TestUI.
    /// </summary>
    public ObservableCollection<RecordedMessage> Messages { get; } = new ObservableCollection<RecordedMessage>();

    public ICommand ClearCommand { get; }

    // Event Handlers
    private void OnMessageReceived(object? sender, MessageEnvelope envelope)
    {
        RecordedMessage message = RecordedMessage.FromEnvelope(_nextSequenceNumber, envelope);

        _nextSequenceNumber++;

        Messages.Add(message);
    }

    private void OnErrorOccurred(object? sender, string error)
    {
        RecordedMessage message = RecordedMessage.FromError(_nextSequenceNumber, error);

        _nextSequenceNumber++;

        Messages.Add(message);
    }
}
