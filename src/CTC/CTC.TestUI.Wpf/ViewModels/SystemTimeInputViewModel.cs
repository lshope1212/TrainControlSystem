using System.Globalization;
using System.Windows.Input;
using CTC.TestUI.Wpf.Commands;
using CTC.TestUI.Wpf.Services;
using TrainControl.Contracts.Messages;

namespace CTC.TestUI.Wpf.ViewModels;

/// <summary>
/// Simulates the shared simulation clock by sending a manually entered time to the
/// running CTC as a <see cref="SystemTimeMessage"/>. CTC has no clock of its own.
/// </summary>
public class SystemTimeInputViewModel : CtcInputViewModelBase
{
    private string _timeText = "06:00:00";

    public SystemTimeInputViewModel(ICtcMessageSender sender, CommunicationStatusViewModel communicationStatus)
        : base(sender, communicationStatus)
    {
        SendCommand = new AsyncRelayCommand(_ => SendAsync());
    }

    /// <summary>Time of simulation day, formatted hh:mm:ss.</summary>
    public string TimeText
    {
        get => _timeText;
        set => SetProperty(ref _timeText, value);
    }

    public ICommand SendCommand { get; }

    private Task SendAsync()
    {
        if (!TimeSpan.TryParseExact(TimeText.Trim(), @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var time))
        {
            Result = "Enter a time as hh:mm:ss.";
            return Task.CompletedTask;
        }

        return SendToCtcAsync(new SystemTimeMessage { SystemTime = time }, $" ({time:hh\\:mm\\:ss})");
    }
}
