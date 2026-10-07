using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using CTC.TestUI.Wpf.Commands;
using CTC.TestUI.Wpf.Services;
using TrainControl.Contracts.Messages;

namespace CTC.TestUI.Wpf.ViewModels;

/// <summary>
/// Simulates the shared system clock: a 1x time-of-day clock that sends each new time to
/// the running CTC as a <see cref="SystemTimeMessage"/>. CTC has no clock of its own; during
/// isolated development this view model is the source of truth for simulation time.
/// </summary>
/// <remarks>
/// Single day only: the clock pauses at 23:59:59 rather than rolling over.
/// If a send is still in progress when the clock ticks (e.g. CTC is busy or not running),
/// that tick's send is skipped and the newest time is sent once the earlier send finishes,
/// so sends never overlap and CTC always ends up with the latest time.
/// </remarks>
public class SystemTimeInputViewModel : CtcInputViewModelBase
{
    private const string TimeFormat = @"hh\:mm\:ss";

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LastTimeOfDay = new TimeSpan(23, 59, 59);

    private readonly DispatcherTimer _timer;
    private readonly CancellationToken _shutdownToken;
    private TimeSpan _startTime = new TimeSpan(12, 0, 0);
    private TimeSpan _currentTime;
    private string _timeText;
    private bool _isRunning;
    private bool _isSending;

    public SystemTimeInputViewModel(ICtcMessageSender sender, CommunicationStatusViewModel communicationStatus, CancellationToken shutdownToken)
        : base(sender, communicationStatus)
    {
        _currentTime = _startTime;
        _timeText = _startTime.ToString(TimeFormat);
        _shutdownToken = shutdownToken;

        // One timer for the view model's lifetime; Start/Pause only start and stop it, so
        // pressing Start repeatedly can never create extra timers.
        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += OnTick;

        // Stop ticking when the application shuts down (the token is cancelled in App.OnExit).
        _shutdownToken.Register(Pause);

        SetTimeCommand = new AsyncRelayCommand(_ => SetTimeAsync(), _ => !IsRunning);
        StartCommand = new RelayCommand(_ => Start(), _ => !IsRunning && _currentTime < LastTimeOfDay);
        PauseCommand = new RelayCommand(_ => Pause(), _ => IsRunning);
        ResetCommand = new AsyncRelayCommand(_ => ResetAsync());
    }

    /// <summary>Time to set the clock to, typed as HH:mm:ss.</summary>
    public string TimeText
    {
        get => _timeText;
        set => SetProperty(ref _timeText, value);
    }

    /// <summary>The simulation clock's current time.</summary>
    public string CurrentTimeDisplay => _currentTime.ToString(TimeFormat);

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsPaused));
                OnPropertyChanged(nameof(ClockState));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>The time can only be edited while the clock is paused.</summary>
    public bool IsPaused => !IsRunning;

    public string ClockState => IsRunning ? "Running" : _currentTime >= LastTimeOfDay ? "Paused (end of day)" : "Paused";

    public ICommand SetTimeCommand { get; }

    public ICommand StartCommand { get; }

    public ICommand PauseCommand { get; }

    public ICommand ResetCommand { get; }

    private Task SetTimeAsync()
    {
        if (!TimeSpan.TryParseExact(TimeText.Trim(), TimeFormat, CultureInfo.InvariantCulture, out var time))
        {
            Result = "Enter a time as HH:mm:ss.";
            return Task.CompletedTask;
        }

        // Set Time also becomes the value Reset returns to.
        _startTime = time;
        SetCurrentTime(time);
        return SendCurrentTimeAsync();
    }

    private void Start()
    {
        if (IsRunning || _currentTime >= LastTimeOfDay)
        {
            return;
        }

        _timer.Start();
        IsRunning = true;
    }

    private void Pause()
    {
        _timer.Stop();
        IsRunning = false;
    }

    private Task ResetAsync()
    {
        Pause();
        SetCurrentTime(_startTime);
        return SendCurrentTimeAsync();
    }

    // async void is unavoidable for a timer event; SendCurrentTimeAsync never throws.
    private async void OnTick(object? sender, EventArgs e)
    {
        SetCurrentTime(_currentTime + TickInterval);

        if (_currentTime >= LastTimeOfDay)
        {
            // No multi-day simulation yet: stop at the end of the day.
            Pause();
        }

        await SendCurrentTimeAsync();
    }

    private void SetCurrentTime(TimeSpan time)
    {
        _currentTime = time > LastTimeOfDay ? LastTimeOfDay : time;
        OnPropertyChanged(nameof(CurrentTimeDisplay));
        OnPropertyChanged(nameof(ClockState));
    }

    /// <summary>Sends the current time, unless a send is already in progress (see remarks).</summary>
    private async Task SendCurrentTimeAsync()
    {
        if (_isSending)
        {
            return;
        }

        _isSending = true;
        try
        {
            TimeSpan sent;
            do
            {
                sent = _currentTime;
                await SendToCtcAsync(new SystemTimeMessage { SystemTime = sent }, $" ({sent.ToString(TimeFormat)})", _shutdownToken);
            }
            while (sent != _currentTime && !_shutdownToken.IsCancellationRequested);
        }
        finally
        {
            _isSending = false;
        }
    }
}
