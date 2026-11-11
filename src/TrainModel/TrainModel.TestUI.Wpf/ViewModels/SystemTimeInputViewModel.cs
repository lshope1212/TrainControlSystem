using System.Windows.Threading;
using TrainControl.Contracts.Messages;
using TrainModel.TestUI.Wpf.Commands;
using TrainModel.TestUI.Wpf.Services;

namespace TrainModel.TestUI.Wpf.ViewModels;

/// <summary>
/// Stands in for the shared system clock (Launcher / CTC). "Send Time" sends one
/// <see cref="SystemTimeMessage"/>; the TEST CLOCK (Start / Pause / Stop, speed) sends one every
/// 100 ms of real time, advancing by 0.1 s x speed, so the Train Model can be driven without the
/// rest of the system. Do not run the test clock while the Launcher is running: the Train Model
/// would then get time from two clocks.
/// </summary>
public sealed class SystemTimeInputViewModel : TrainModelInputViewModelBase
{
    private static readonly TimeSpan SendInterval = TimeSpan.FromMilliseconds(100);
    private readonly DispatcherTimer _timer;
    private string _timeText = "06:00:00";
    private TimeSpan _time = TimeSpan.FromHours(6);
    private double _speed = 1.0;
    private bool _running;
    private bool _paused;
    private long _sequence;

    public SystemTimeInputViewModel(ITrainModelMessageSender sender, Func<string> trainIdProvider)
        : base(sender, trainIdProvider)
    {
        _timer = new DispatcherTimer { Interval = SendInterval };
        _timer.Tick += async (_, _) => await TickAsync();
        StartCommand = new RelayCommand(_ => Start());
        PauseCommand = new RelayCommand(_ => TogglePause());
        StopCommand = new RelayCommand(_ => Stop());
    }

    public string TimeText { get => _timeText; set => SetProperty(ref _timeText, value); }

    public IReadOnlyList<double> Speeds { get; } = new[] { 1.0, 2.0, 5.0, 10.0 };

    public double Speed { get => _speed; set => SetProperty(ref _speed, value); }

    public RelayCommand StartCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand StopCommand { get; }

    public string ClockStatus => !_running ? "Test clock stopped" : _paused ? "Test clock paused" : $"Test clock running {Speed:0.#}x";

    protected override Task SendAsync()
    {
        _time = TimeSpan.TryParse(TimeText, out var ts) ? ts : TimeSpan.Zero;
        return SendTimeAsync($"Sent time {TimeText}");
    }

    private void Start()
    {
        if (!_running)
        {
            _time = TimeSpan.TryParse(TimeText, out var ts) ? ts : _time;
        }

        _running = true;
        _paused = false;
        _timer.Start();
        OnPropertyChanged(nameof(ClockStatus));
    }

    private void TogglePause()
    {
        if (!_running) return;
        _paused = !_paused;
        OnPropertyChanged(nameof(ClockStatus));
    }

    private void Stop()
    {
        _running = false;
        _timer.Stop();
        OnPropertyChanged(nameof(ClockStatus));
    }

    private async Task TickAsync()
    {
        if (!_paused)
        {
            _time += TimeSpan.FromSeconds(SendInterval.TotalSeconds * Speed);
        }

        TimeText = _time.ToString(@"hh\:mm\:ss");
        await SendTimeAsync($"Test clock {TimeText}");
    }

    private Task SendTimeAsync(string okText) =>
        SendMessageAsync(new SystemTimeMessage
        {
            SystemTime = _time,
            SpeedMultiplier = Speed,
            IsPaused = _running && _paused,
            Sequence = ++_sequence,
        }, okText);
}
