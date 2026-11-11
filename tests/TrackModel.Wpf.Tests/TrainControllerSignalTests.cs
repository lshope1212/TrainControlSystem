using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Threading;
using TrackModel.Core.Services;
using TrackModel.Wpf.Services;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;
using TrainController.Integration;
using TrainController.Integration.Communication;
using TrainController.Integration.Output;

namespace TrackModel.Wpf.Tests;

/// <summary>Production Track Model publisher over isolated pipes into the unchanged Train Controller.</summary>
[TestClass]
public class TrainControllerSignalTests
{
    [TestMethod]
    public async Task EveryLightAspect_ReachesTheExistingTrainControllerAndItsDisplay()
    {
        await using var session = await Session.StartAsync();
        foreach (var aspect in new[] { SignalState.Red, SignalState.Yellow, SignalState.Green, SignalState.SuperGreen })
        {
            await session.ApplyAsync(track => track.ApplyCommand(new() { BlockId = "6", Signal = aspect }));
            await session.UntilSignalAsync("001", "6", aspect);
            Assert.AreEqual(aspect, await session.ControllerLightAsync("001"));
        }
        Assert.IsTrue(session.Received.All(message => message.TrainId == "001"));
    }

    [TestMethod]
    public async Task MovementOffASignalAndTrainRemoval_ClearThePreviousGreen()
    {
        await using var session = await Session.StartAsync();
        await session.ApplyAsync(track => track.ApplyCommand(new() { BlockId = "6", Signal = SignalState.Green }));
        await session.UntilSignalAsync("001", "6", SignalState.Green);
        await session.ApplyAsync(track => track.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "7" }));
        await session.UntilSignalAsync("001", "7", SignalState.Unknown);
        Assert.AreEqual(SignalState.Unknown, await session.ControllerLightAsync("001"));

        await session.ApplyAsync(track =>
        {
            track.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "6" });
            track.ApplyCommand(new() { BlockId = "6", Signal = SignalState.Green });
        });
        await session.UntilSignalAsync("001", "6", SignalState.Green);
        await session.ApplyAsync(track => track.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "" }));
        await session.UntilSignalAsync("001", "6", SignalState.Unknown);
        Assert.AreEqual(SignalState.Unknown, await session.ControllerLightAsync("001"));
    }

    [TestMethod]
    public async Task PowerFailureAndRecovery_UpdateTheEffectiveLight()
    {
        await using var session = await Session.StartAsync();
        await session.ApplyAsync(track => track.ApplyCommand(new() { BlockId = "6", Signal = SignalState.Green }));
        await session.UntilSignalAsync("001", "6", SignalState.Green);
        await session.ApplyAsync(track => track.ApplyFailures(new() { BlockId = "6", PowerFailure = true }));
        await session.UntilSignalAsync("001", "6", SignalState.Unknown);
        Assert.AreEqual(SignalState.Unknown, await session.ControllerLightAsync("001"));
        await session.ApplyAsync(track => track.ApplyFailures(new() { BlockId = "6" }));
        await session.UntilSignalAsync("001", "6", SignalState.Green);
        Assert.AreEqual(SignalState.Green, await session.ControllerLightAsync("001"));
    }

    [TestMethod]
    public async Task LateTrainController_ReceivesTheCurrentLightOnHeartbeat()
    {
        await using var session = await Session.StartAsync(startReceiver: false);
        await session.ApplyAsync(track => track.ApplyCommand(new() { BlockId = "6", Signal = SignalState.Yellow }));
        await Task.Delay(350);
        session.StartReceiver();
        await session.UntilSignalAsync("001", "6", SignalState.Yellow);
        Assert.AreEqual(SignalState.Yellow, await session.ControllerLightAsync("001"));
        var before = session.Received.Count;
        await UntilAsync(() => session.Received.Count > before, "Idle heartbeat did not refresh the light.");
    }

    [TestMethod]
    public async Task DisconnectAndDroppedSnapshots_RetainTheClearingMessageForARemovedTrain()
    {
        await using var session = await Session.StartAsync();
        await session.ApplyAsync(track => track.ApplyCommand(new() { BlockId = "6", Signal = SignalState.Green }));
        await session.UntilSignalAsync("001", "6", SignalState.Green);
        await session.StopReceiverAsync();
        await session.ApplyAsync(track =>
        {
            track.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "" });
            track.ApplyTrainUpdate(new() { TrainId = "003", CurrentBlockId = "11" });
            track.ApplyCommand(new() { BlockId = "11", Signal = SignalState.Yellow });
        });
        await Task.Delay(350);
        session.StartReceiver();
        await session.UntilSignalAsync("001", "6", SignalState.Unknown);
        await session.UntilSignalAsync("003", "11", SignalState.Yellow);
        Assert.AreEqual(SignalState.Unknown, await session.ControllerLightAsync("001"));
        Assert.AreEqual(SignalState.Yellow, await session.ControllerLightAsync("003"));
    }

    [TestMethod]
    public async Task LayoutReplacement_ClearsTheOldTrainsLight()
    {
        await using var session = await Session.StartAsync();
        await session.ApplyAsync(track => track.ApplyCommand(new() { BlockId = "6", Signal = SignalState.Green }));
        await session.UntilSignalAsync("001", "6", SignalState.Green);
        await session.ApplyAsync(track => track.LoadLayout(BlueLineTrackLayout.Create()));
        await session.UntilSignalAsync("001", "6", SignalState.Unknown);
        Assert.AreEqual(SignalState.Unknown, await session.ControllerLightAsync("001"));
    }

    [TestMethod]
    public async Task TrainLabels_AreNormalizedAndUnrelatedBlocksNeverOverwriteAnotherTrain()
    {
        await using var session = await Session.StartAsync(track =>
        {
            track.ApplyTrainUpdate(new() { TrainId = "TRAIN-001", CurrentBlockId = "6" });
            track.ApplyTrainUpdate(new() { TrainId = "003", CurrentBlockId = "11" });
            track.ApplyTrainUpdate(new() { TrainId = "01", CurrentBlockId = "1" });
            track.ApplyCommand(new() { BlockId = "6", Signal = SignalState.Green });
            track.ApplyCommand(new() { BlockId = "11", Signal = SignalState.Yellow });
        });
        await session.UntilSignalAsync("001", "6", SignalState.Green);
        await session.UntilSignalAsync("003", "11", SignalState.Yellow);
        Assert.AreEqual(SignalState.Green, await session.ControllerLightAsync("001"));
        Assert.AreEqual(SignalState.Yellow, await session.ControllerLightAsync("003"));
        Assert.IsTrue(session.Received.All(message => message.TrainId is "001" or "003"));
    }

    [TestMethod]
    public async Task ConflictingAliases_ReportUnknownInsteadOfPickingAnArbitraryLight()
    {
        await using var session = await Session.StartAsync(track =>
        {
            track.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "6" });
            track.ApplyTrainUpdate(new() { TrainId = "TRAIN-001", CurrentBlockId = "11" });
            track.ApplyCommand(new() { BlockId = "6", Signal = SignalState.Green });
        });
        await session.UntilSignalAsync("001", "", SignalState.Unknown);
        Assert.AreEqual(SignalState.Unknown, await session.ControllerLightAsync("001"));
    }

    private static async Task UntilAsync(Func<bool> condition, string reason)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.IsLessThan(5, timer.Elapsed.TotalSeconds, reason);
            await Task.Delay(20);
        }
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _thread;
        private readonly Dispatcher _dispatcher;
        private readonly TrackService _track;
        private readonly TrackModelConnection _connection;
        private readonly TrackModelEndpoints _endpoints;
        private readonly TrainControllerSubsystem _controller = TrainControllerSubsystem.Create(trainModelSink: new SilentSink());
        private readonly TrainControllerPipeReceiver _receiver;
        private readonly Dictionary<string, long> _ticks = [];
        private CancellationTokenSource? _receiverStop;
        private Task? _listen;
        private Task? _run;
        public ConcurrentQueue<TrackModelSignalMessage> Received { get; } = new();

        private Session(Action<TrackService>? setup)
        {
            var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(() => { ready.SetResult(Dispatcher.CurrentDispatcher); Dispatcher.Run(); }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _dispatcher = ready.Task.GetAwaiter().GetResult();
            var prefix = "TrackModel.SignalTest." + Guid.NewGuid().ToString("N");
            _endpoints = new(prefix, prefix + ".Wayside", prefix + ".TrainModel", prefix + ".Ctc", prefix + ".TestUi");
            _receiver = new TrainControllerPipeReceiver(_controller);
            (_track, _connection) = _dispatcher.Invoke(() =>
            {
                var track = new TrackService();
                track.LoadLayout(BlueLineTrackLayout.Create());
                if (setup is null) track.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "6" });
                else setup(track);
                return (track, new TrackModelConnection(track, _dispatcher, _endpoints, TimeSpan.FromMilliseconds(100)));
            });
        }

        public static async Task<Session> StartAsync(Action<TrackService>? setup = null, bool startReceiver = true)
        {
            var session = new Session(setup);
            if (startReceiver) session.StartReceiver();
            session._run = await session._dispatcher.InvokeAsync(() => session._connection.RunAsync(session._stop.Token));
            return session;
        }

        public void StartReceiver()
        {
            _receiverStop = new CancellationTokenSource();
            var token = _receiverStop.Token;
            _listen = NamedPipeTransport.ListenAsync(_endpoints.TrainController, async envelope =>
            {
                await _receiver.HandleAsync(envelope, token);
                Received.Enqueue(MessageSerializer.DeserializePayload<TrackModelSignalMessage>(envelope));
            }, error => Assert.Fail(error.ToString()), token);
        }

        public async Task StopReceiverAsync()
        {
            if (_receiverStop is null) return;
            await _receiverStop.CancelAsync();
            await _listen!;
            _receiverStop.Dispose();
            _receiverStop = null;
        }

        public Task ApplyAsync(Action<TrackService> action) => _dispatcher.InvokeAsync(() => action(_track)).Task;

        public Task UntilSignalAsync(string trainId, string blockId, SignalState aspect) => UntilAsync(() =>
        {
            var latest = Received.LastOrDefault(message => message.TrainId == trainId);
            return latest is not null && latest.BlockId == blockId && latest.Signal == aspect;
        }, $"No {aspect} light received for {trainId} at {blockId}.");

        public async Task<SignalState> ControllerLightAsync(string trainId)
        {
            _ticks.TryGetValue(trainId, out var previous);
            _ticks[trainId] = previous + 1;
            await _receiver.HandleStatusAsync(new TrainModelStatusMessage
            {
                TrainId = trainId, TickId = previous + 1, IsActive = true,
                ActualSpeedMetersPerSecond = 0, CommandedSpeedMetersPerSecond = 5,
                CommandedAuthorityMeters = 100, TrackSignalValid = true, TrafficLight = SignalState.Unknown
            }, CancellationToken.None);
            return _controller.Registry.Get("TRAIN-" + trainId).LastOutput!.Display.TrafficLight;
        }

        public async ValueTask DisposeAsync()
        {
            await _dispatcher.InvokeAsync(_connection.Stop);
            await _stop.CancelAsync();
            if (_run is not null) await _run.WaitAsync(TimeSpan.FromSeconds(5));
            await StopReceiverAsync();
            _dispatcher.InvokeShutdown();
            _thread.Join(TimeSpan.FromSeconds(5));
            _stop.Dispose();
        }
    }

    private sealed class SilentSink : ITrainModelCommandSink
    {
        public Task SendAsync(TrainControllerCommandMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
