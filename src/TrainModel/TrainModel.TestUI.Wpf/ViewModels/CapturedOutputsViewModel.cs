using System.Collections.ObjectModel;
using System.Windows.Threading;
using TrainModel.TestUI.Wpf.Commands;
using TrainModel.TestUI.Wpf.Models;

namespace TrainModel.TestUI.Wpf.ViewModels;

/// <summary>
/// Holds the list of messages the Train Model has sent out (captured on the fake
/// neighbour pipes this harness hosts). Thread-marshals onto the UI dispatcher.
/// </summary>
public sealed class CapturedOutputsViewModel : ViewModelBase
{
    private readonly Dispatcher _dispatcher;
    private int _sequence;

    public CapturedOutputsViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ClearCommand = new RelayCommand(_ => Messages.Clear());
    }

    public ObservableCollection<RecordedMessage> Messages { get; } = new();

    public RelayCommand ClearCommand { get; }

    /// <summary>Record one captured outbound message (safe from any thread).</summary>
    public void Record(string messageType, string description)
    {
        void Add()
        {
            Messages.Insert(0, new RecordedMessage
            {
                SequenceNumber = ++_sequence,
                MessageType = messageType,
                Description = description,
            });
            OnPropertyChanged(nameof(Messages));
        }

        if (_dispatcher.CheckAccess()) Add();
        else _dispatcher.Invoke(Add);
    }
}
