using TrainControl.Contracts.Messages;
using TrainModel.TestUI.Wpf.Services;

namespace TrainModel.TestUI.Wpf.ViewModels;

/// <summary>Stands in for the shared system clock: sends a <see cref="SystemTimeMessage"/>.</summary>
public sealed class SystemTimeInputViewModel : TrainModelInputViewModelBase
{
    private string _timeText = "06:00:00";

    public SystemTimeInputViewModel(ITrainModelMessageSender sender, Func<string> trainIdProvider)
        : base(sender, trainIdProvider) { }

    public string TimeText { get => _timeText; set => SetProperty(ref _timeText, value); }

    protected override Task SendAsync()
    {
        var msg = new SystemTimeMessage
        {
            SystemTime = TimeSpan.TryParse(TimeText, out var ts) ? ts : TimeSpan.Zero,
        };
        return SendMessageAsync(msg, $"Sent time {TimeText}");
    }
}
