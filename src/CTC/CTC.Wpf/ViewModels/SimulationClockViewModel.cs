using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using CTC.Core.Scheduling;
using CTC.Wpf.Commands;
using CTC.Wpf.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace CTC.Wpf.ViewModels;

/// <summary>
/// The dispatcher's simulation-clock controls. CTC is the only module allowed to control the
/// shared clock, but it does not own it: every button sends a
/// <see cref="SimulationTimeControlMessage"/> request, and everything displayed here comes from
/// the authoritative <see cref="SystemTimeMessage"/> the clock distributes afterwards
/// (<see cref="ApplyClockState"/>), never from what was requested.
/// </summary>
public sealed class SimulationClockViewModel : ViewModelBase
{
    /// <summary>No clock state for this long means the clock (Launcher) is not running.</summary>
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(2);

    private readonly ISimulationClockControlSender _sender;
    private SystemTimeMessage? _state;
    private DateTime _lastReceivedUtc;
    private bool _isClockConnected;
    private string _editedTime = "12:00:00";
    private string _controlStatus = "Waiting for the simulation clock.";

    public SimulationClockViewModel(ISimulationClockControlSender sender)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));

        ResumeSimulationCommand = new AsyncRelayCommand(
            _ => RequestAsync(new SimulationTimeControlMessage { Command = SimulationClockCommand.Run }, "run"),
            _ => IsSimulationPaused);
        PauseSimulationCommand = new AsyncRelayCommand(
            _ => RequestAsync(new SimulationTimeControlMessage { Command = SimulationClockCommand.Pause }, "pause"),
            _ => IsClockConnected && !IsSimulationPaused);
        SetSimulationSpeedCommand = new AsyncRelayCommand(SetSpeedAsync);
        SetSimulationTimeCommand = new AsyncRelayCommand(_ => SetTimeAsync());
        ResetSimulationTimeCommand = new AsyncRelayCommand(
            _ => RequestAsync(new SimulationTimeControlMessage { Command = SimulationClockCommand.Reset }, "reset"));

        // Only notices that clock updates stopped arriving; it never advances any time itself.
        var connectionCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        connectionCheck.Tick += (_, _) => IsClockConnected = _state is not null && DateTime.UtcNow - _lastReceivedUtc < ConnectionTimeout;
        connectionCheck.Start();
    }

    /// <summary>Speeds offered to the dispatcher (the clock supports 1x..10x).</summary>
    public IReadOnlyList<double> SpeedOptions { get; } = new[] { 1.0, 2.0, 5.0, 10.0 };

    /// <summary>True while authoritative clock state is arriving.</summary>
    public bool IsClockConnected
    {
        get => _isClockConnected;
        private set
        {
            if (SetProperty(ref _isClockConnected, value))
            {
                OnPropertyChanged(nameof(ClockStatusDisplay));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>Paused as last reported by the clock; also true before any state has arrived.</summary>
    public bool IsSimulationPaused => _state?.IsPaused ?? true;

    public double? SimulationSpeed => _state?.SpeedMultiplier;

    public string SimulationSpeedDisplay => _state is null ? "--" : $"{_state.SpeedMultiplier:0.#}x";

    public string ClockStatusDisplay =>
        !IsClockConnected ? "Clock not connected"
        : IsSimulationPaused ? "Paused"
        : "Running";

    /// <summary>Time for <see cref="SetSimulationTimeCommand"/>, typed as HH:mm:ss.</summary>
    public string EditedTime
    {
        get => _editedTime;
        set => SetProperty(ref _editedTime, value ?? string.Empty);
    }

    /// <summary>Outcome of the latest clock request (delivery only, not the clock's response).</summary>
    public string ControlStatus
    {
        get => _controlStatus;
        private set => SetProperty(ref _controlStatus, value);
    }

    public ICommand ResumeSimulationCommand { get; }

    public ICommand PauseSimulationCommand { get; }

    /// <summary>Parameter: the requested speed multiplier (e.g. "5").</summary>
    public ICommand SetSimulationSpeedCommand { get; }

    public ICommand SetSimulationTimeCommand { get; }

    public ICommand ResetSimulationTimeCommand { get; }

    /// <summary>Adopts the clock's authoritative state. Called on the UI thread for every received SystemTimeMessage.</summary>
    public void ApplyClockState(SystemTimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        bool wasPaused = IsSimulationPaused;
        _state = message;
        _lastReceivedUtc = DateTime.UtcNow;
        IsClockConnected = true;

        OnPropertyChanged(nameof(SimulationSpeed));
        OnPropertyChanged(nameof(SimulationSpeedDisplay));
        if (wasPaused != IsSimulationPaused)
        {
            OnPropertyChanged(nameof(IsSimulationPaused));
            OnPropertyChanged(nameof(ClockStatusDisplay));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private Task SetSpeedAsync(object? parameter)
    {
        double speed;
        try
        {
            speed = Convert.ToDouble(parameter, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException)
        {
            ControlStatus = "Choose a simulation speed.";
            return Task.CompletedTask;
        }

        return RequestAsync(new SimulationTimeControlMessage { Command = SimulationClockCommand.SetSpeed, SpeedMultiplier = speed }, $"speed {speed:0.#}x");
    }

    private Task SetTimeAsync()
    {
        if (!ScheduleTemplateConverter.TryParseTime(EditedTime, out var time))
        {
            ControlStatus = $"Enter the simulation time as {ScheduleTemplateConverter.TimeFormat}.";
            return Task.CompletedTask;
        }

        return RequestAsync(new SimulationTimeControlMessage { Command = SimulationClockCommand.SetTime, SystemTime = time }, $@"time {time:hh\:mm\:ss}");
    }

    private async Task RequestAsync(SimulationTimeControlMessage request, string description)
    {
        try
        {
            await _sender.SendAsync(request);

            // Only the request was delivered; the displayed state changes when the clock reports it.
            ControlStatus = $"Requested {description}.";
        }
        catch (Exception ex)
        {
            // Everything is caught because this runs from an async void command and must never crash the UI.
            ControlStatus = $"Unable to request {description}: {ex.Message}";
        }
    }
}
