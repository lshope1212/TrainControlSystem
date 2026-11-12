using System.Collections.Concurrent;
using TrainControl.Common.Communication;
using TrainControl.Common.Identity;
using TrainControl.Contracts.Messages;
using TrainController.Integration;
using TrainController.Integration.Communication;
using TrainController.Integration.Output;
using TrainModel.Core.Models;
using TrainModel.Core.Services;
using TrainModel.Wpf.Services;
using TrainModel.Wpf.ViewModels;

namespace TrainControl.Integration.Tests;

/// <summary>
/// The production Train Model application layer (TrainModel.Wpf's view model: fleet, physics stepping,
/// pipe receiver and output publisher) on its own UI thread, wired to a <see cref="LiveSystem"/> over its
/// isolated pipes. Only the window is missing; the physics is the real <see cref="TrainSimulationService"/>.
/// </summary>
internal sealed class LiveTrainModel : IAsyncDisposable
{
    private readonly DispatcherThread _thread;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;

    private LiveTrainModel(DispatcherThread thread, TrainFleetService fleet, MainWindowViewModel viewModel, string inboundPipe)
    {
        _thread = thread;
        Fleet = fleet;
        ViewModel = viewModel;
        var receiver = new TrainModelNamedPipeReceiver(viewModel, thread.Dispatcher, inboundPipe);
        var token = _stop.Token;
        _run = thread.StartAsync(() => receiver.RunAsync(token));
    }

    public TrainFleetService Fleet { get; }

    public MainWindowViewModel ViewModel { get; }

    /// <summary>Starts it as TrainModel.Wpf's App does (view model plus receiver), minus the window.</summary>
    public static async Task<LiveTrainModel> StartAsync(LiveSystem system)
    {
        var thread = new DispatcherThread("Train Model UI");
        var fleet = new TrainFleetService();
        var viewModel = await thread.GetAsync(() => new MainWindowViewModel(fleet, new TrainModelOutputPublisher(system.TrackModelPipe, system.TrainControllerPipe)));
        return new LiveTrainModel(thread, fleet, viewModel, system.TrainModelPipe);
    }

    /// <summary>The train's state as the Train Model holds it, read on its own thread; null when it has no such train.</summary>
    public Task<TrainState?> StateAsync(string trainId) => _thread.GetAsync(() => Fleet.GetTrain(trainId)?.CurrentState);

    /// <summary>Trains in the Train Model's roster, read on its own thread.</summary>
    public Task<string[]> RosterAsync() => _thread.GetAsync(() => ViewModel.Roster.Select(row => row.TrainId).ToArray());

    /// <summary>The Train Model's event log, newest first.</summary>
    public Task<string[]> LogAsync() => _thread.GetAsync(() => ViewModel.EventLog.Select(item => $"{item.Severity} {item.Source}: {item.Message}").ToArray());

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }

        _thread.Dispose();
        _stop.Dispose();
    }
}

/// <summary>
/// The production Train Controller (subsystem, Normal-mode receiver, software controller) listening on the
/// system's Train Controller pipe and commanding the Train Model over its pipe, exactly as TrainController.Wpf
/// does with <see cref="NamedPipeTrainModelCommandSink"/>; only the pipe names are the test's.
/// </summary>
internal sealed class LiveTrainController : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _listen;

    public LiveTrainController(LiveSystem system)
    {
        Sink = new PipeCommandSink(system.TrainModelPipe);
        Subsystem = TrainControllerSubsystem.Create(trainModelSink: Sink);
        Receiver = new TrainControllerPipeReceiver(Subsystem);
        var token = _stop.Token;
        _listen = NamedPipeTransport.ListenAsync(system.TrainControllerPipe, envelope => Receiver.HandleAsync(envelope, token), _ => { }, token);
    }

    public TrainControllerSubsystem Subsystem { get; }

    public TrainControllerPipeReceiver Receiver { get; }

    public PipeCommandSink Sink { get; }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _listen.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }

        _stop.Dispose();
    }

    /// <summary><see cref="NamedPipeTrainModelCommandSink"/> on the test's Train Model pipe, recording what it sent.</summary>
    internal sealed class PipeCommandSink(string trainModelPipe) : ITrainModelCommandSink
    {
        public ConcurrentQueue<TrainControllerCommandMessage> Sent { get; } = new();

        public async Task SendAsync(TrainControllerCommandMessage message, CancellationToken cancellationToken)
        {
            if (TrainIdConvention.TryNormalize(message.TrainId, out var canonical))
            {
                message.TrainId = canonical;
            }

            await NamedPipeTransport.SendAsync(trainModelPipe, message, cancellationToken, NamedPipeTrainModelCommandSink.ConnectTimeoutMilliseconds);
            Sent.Enqueue(message);
        }
    }
}

/// <summary>
/// Stands in for the Launcher's shared simulation clock: every 100 ms of wall time it advances simulated time
/// by 0.1 s times <see cref="SpeedMultiplier"/> and sends the <see cref="SystemTimeMessage"/> to each module pipe.
/// </summary>
internal sealed class ClockPump : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;
    private long _ticks;

    public ClockPump(TimeSpan start, double speedMultiplier, params string[] pipes)
    {
        _ticks = start.Ticks;
        SpeedMultiplier = speedMultiplier;
        _run = Task.Run(() => RunAsync(pipes, _stop.Token));
    }

    public double SpeedMultiplier { get; }

    public TimeSpan Now => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    /// <summary>Development diagnostics (printed with TRACKCONTROLLER_PERF=1): clock delivery per subscriber pipe.</summary>
    private sealed class Delivery
    {
        public int Sent, Missed;
        public TimeSpan LongestSend, LongestGap, Last;
    }

    private readonly ConcurrentDictionary<string, Delivery> _deliveries = new();
    private readonly System.Diagnostics.Stopwatch _wall = System.Diagnostics.Stopwatch.StartNew();
    private TimeSpan _startTime;

    /// <summary>How each subscriber received the clock: delivered/missed sends, slowest send, longest gap, simulated vs wall rate.</summary>
    public string DeliveryReport() => $"Clock: {(Now - _startTime).TotalSeconds / Math.Max(0.001, _wall.Elapsed.TotalSeconds):0.00} simulated s per wall s "
        + $"(nominal {SpeedMultiplier:0.#}) over {_wall.Elapsed.TotalSeconds:0} s; "
        + string.Join("; ", _deliveries.Select(item => $"{item.Key.Split('.')[^1]}: {item.Value.Sent} delivered, {item.Value.Missed} missed, "
            + $"slowest send {item.Value.LongestSend.TotalMilliseconds:0} ms, longest gap {item.Value.LongestGap.TotalMilliseconds:0} ms"));

    private async Task RunAsync(string[] pipes, CancellationToken stop)
    {
        _startTime = Now;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var message = new SystemTimeMessage { SystemTime = Now, SpeedMultiplier = SpeedMultiplier, IsPaused = false };
                foreach (string pipe in pipes)
                {
                    var delivery = _deliveries.GetOrAdd(pipe, _ => new Delivery());
                    var started = _wall.Elapsed;
                    try
                    {
                        await NamedPipeTransport.SendAsync(pipe, message, stop, connectTimeoutMilliseconds: 100);
                        var done = _wall.Elapsed;
                        delivery.Sent++;
                        if (done - started > delivery.LongestSend) delivery.LongestSend = done - started;
                        if (delivery.Sent > 1 && done - delivery.Last > delivery.LongestGap) delivery.LongestGap = done - delivery.Last;
                        delivery.Last = done;
                    }
                    catch (Exception ex) when (ex is TimeoutException or IOException)
                    {
                        delivery.Missed++;
                    }
                }

                await Task.Delay(100, stop);
                Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(0.1 * SpeedMultiplier).Ticks);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _run;
        }
        catch (OperationCanceledException)
        {
        }

        if (Environment.GetEnvironmentVariable("TRACKCONTROLLER_PERF") == "1")
        {
            Console.WriteLine(DeliveryReport());
        }

        _stop.Dispose();
    }
}
