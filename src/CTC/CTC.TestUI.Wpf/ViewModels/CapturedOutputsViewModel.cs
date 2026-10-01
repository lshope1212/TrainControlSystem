using System.Collections.ObjectModel;
using System.Windows.Input;
using CTC.TestUI.Wpf.Commands;
using CTC.TestUI.Wpf.Models;
using CTC.TestUI.Wpf.Services;

namespace CTC.TestUI.Wpf.ViewModels;

/// <summary>
/// Shows every message CTC.Core passed to its IMessageSender, as captured by the
/// <see cref="RecordingMessageSender"/>.
/// </summary>
public class CapturedOutputsViewModel : ViewModelBase
{
    private readonly RecordingMessageSender _sender;

    public CapturedOutputsViewModel(RecordingMessageSender sender)
    {
        _sender = sender;
        ClearCommand = new RelayCommand(_ => _sender.Clear(), _ => _sender.Messages.Count > 0);
    }

    public ObservableCollection<RecordedMessage> Messages => _sender.Messages;

    public ICommand ClearCommand { get; }
}
