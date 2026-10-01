using System.Globalization;
using System.Windows.Input;
using CTC.Core.Interfaces;
using CTC.TestUI.Wpf.Commands;

namespace CTC.TestUI.Wpf.ViewModels;

/// <summary>
/// Simulates the shared simulation clock by pushing a manually entered time into
/// CTCService.SetSystemTime. CTC has no clock of its own.
/// </summary>
public class SystemTimeInputViewModel : ViewModelBase
{
    private readonly ICTCService _ctc;
    private readonly Action _onCtcStateChanged;
    private string _timeText = "06:00:00";
    private string _result = string.Empty;

    public SystemTimeInputViewModel(ICTCService ctc, Action onCtcStateChanged)
    {
        _ctc = ctc;
        _onCtcStateChanged = onCtcStateChanged;
        ApplyCommand = new RelayCommand(_ => Apply());
    }

    /// <summary>Time of simulation day, formatted hh:mm:ss.</summary>
    public string TimeText
    {
        get => _timeText;
        set => SetProperty(ref _timeText, value);
    }

    public ICommand ApplyCommand { get; }

    public string Result
    {
        get => _result;
        private set => SetProperty(ref _result, value);
    }

    private void Apply()
    {
        if (!TimeSpan.TryParseExact(TimeText.Trim(), @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var time))
        {
            Result = "Enter a time as hh:mm:ss.";
            return;
        }

        _ctc.SetSystemTime(time);
        Result = $"SetSystemTime({time:hh\\:mm\\:ss}).";
        _onCtcStateChanged();
    }
}
