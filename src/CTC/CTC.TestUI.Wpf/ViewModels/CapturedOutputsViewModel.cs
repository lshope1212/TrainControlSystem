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

        // The receiver raises its events on the UI thread, so the collection is safe to modify.
        receiver.MessageReceived += OnMessageReceived;
        receiver.ErrorOccurred += OnErrorOccurred;

        // Sequence numbers keep increasing across a clear so rows are never confused.
        ClearCommand = new RelayCommand(_ => Messages.Clear(), _ => Messages.Count > 0);
    }

    public ObservableCollection<RecordedMessage> Messages { get; } = new ObservableCollection<RecordedMessage>();

    public ICommand ClearCommand { get; }

    private void OnMessageReceived(object? sender, MessageEnvelope envelope) =>
        Messages.Add(RecordedMessage.FromEnvelope(_nextSequenceNumber++, envelope));

    private void OnErrorOccurred(object? sender, string error) =>
        Messages.Add(RecordedMessage.FromError(_nextSequenceNumber++, error));
}
