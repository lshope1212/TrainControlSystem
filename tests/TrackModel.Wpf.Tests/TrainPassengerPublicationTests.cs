using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Threading;
using TrackModel.Core.Services;
using TrackModel.Wpf.Services;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Messages;

namespace TrackModel.Wpf.Tests;

[TestClass]
public class TrainPassengerPublicationTests
{
    [TestMethod]
    public async Task UnchangedDemandIsThrottled_WhileEveryRequestedEnvironmentStillArrives()
    {
        await using var session = await Session.StartAsync();
        await session.UntilAsync(() => session.Demand.LastOrDefault()?.WaitingToBoard == 24);
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            long previous = session.Environments.Last().Sequence;
            await session.ChangeAsync(_ => { });
            await session.UntilAsync(() => session.Environments.Last().Sequence > previous);
        }
        int allowance = (int)(timer.Elapsed.TotalSeconds / TrackModelConnection.DefaultFeedbackHeartbeat.TotalSeconds) + 3;
        Assert.IsLessThanOrEqualTo(allowance, session.Demand.Count,
            "Passenger demand is sent on change or heartbeat, rather than flooding each control-data snapshot.");
    }

    [TestMethod]
    public async Task NewDemandArrivesImmediately_AndLeavingOrRemovingTheTrainClearsIt()
    {
        await using var session = await Session.StartAsync();
        await session.UntilAsync(() => session.Demand.LastOrDefault()?.WaitingToBoard == 24);
        await session.ChangeAsync(track => track.RecordStationArrivals("10", 6));
        await session.UntilAsync(() => session.Demand.LastOrDefault()?.WaitingToBoard == 30);
        await session.ChangeAsync(track => track.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "11" }));
        await session.UntilAsync(() => session.Demand.LastOrDefault()?.WaitingToBoard == 0);
        await session.ChangeAsync(track => track.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "" }));
        await session.UntilAsync(() => session.ClearedBeacons.Any(b => b.TrainId == "001"));
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _thread;
        private readonly Dispatcher _dispatcher;
        private readonly TrackService _track;
        private readonly TrackModelConnection _connection;
        private Task _run = Task.CompletedTask;
        private Task _listen = Task.CompletedTask;
        public ConcurrentQueue<StationPassengerMessage> Demand { get; } = new();
        public ConcurrentQueue<TrackModelTrainEnvironmentMessage> Environments { get; } = new();
        public ConcurrentQueue<BeaconMessage> ClearedBeacons { get; } = new();

        private Session()
        {
            var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(() => { ready.SetResult(Dispatcher.CurrentDispatcher); Dispatcher.Run(); }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _dispatcher = ready.Task.GetAwaiter().GetResult();
            var prefix = "TrackModel.PassengerPublication." + Guid.NewGuid().ToString("N");
            var endpoints = new TrackModelEndpoints(prefix, prefix + ".Wayside", prefix + ".Train", prefix + ".Ctc", prefix + ".TestUi");
            (_track, _connection) = _dispatcher.Invoke(() =>
            {
                var track = new TrackService();
                track.LoadLayout(BlueLineTrackLayout.Create());
                track.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "10" });
                return (track, new TrackModelConnection(track, _dispatcher, endpoints, TimeSpan.FromMilliseconds(100)));
            });
            _listen = NamedPipeTransport.ListenAsync(endpoints.TrainModel, envelope =>
            {
                switch (envelope.MessageType)
                {
                    case nameof(StationPassengerMessage): Demand.Enqueue(MessageSerializer.DeserializePayload<StationPassengerMessage>(envelope)); break;
                    case nameof(BeaconMessage): ClearedBeacons.Enqueue(MessageSerializer.DeserializePayload<BeaconMessage>(envelope)); break;
                    case nameof(TrackModelTrainEnvironmentMessage):
                        var environment = MessageSerializer.DeserializePayload<TrackModelTrainEnvironmentMessage>(envelope);
                        if (environment.TrainId == "001") Environments.Enqueue(environment);
                        break;
                }
                return Task.CompletedTask;
            }, _ => { }, _stop.Token);
        }

        public static async Task<Session> StartAsync()
        {
            var session = new Session();
            session._run = await session._dispatcher.InvokeAsync(() => session._connection.RunAsync(session._stop.Token));
            await session.UntilAsync(() => !session.Environments.IsEmpty);
            return session;
        }

        public Task ChangeAsync(Action<TrackService> apply) => _dispatcher.InvokeAsync(() => { apply(_track); _connection.RequestSnapshot(); }).Task;

        public async Task UntilAsync(Func<bool> condition)
        {
            var timer = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.IsLessThan(5, timer.Elapsed.TotalSeconds, "The Track Model's passenger/control inputs did not arrive.");
                await Task.Delay(10);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _dispatcher.InvokeAsync(_connection.Stop);
            await _stop.CancelAsync();
            await Task.WhenAll(_run, _listen).WaitAsync(TimeSpan.FromSeconds(5));
            _dispatcher.InvokeShutdown();
            _thread.Join(TimeSpan.FromSeconds(5));
            _stop.Dispose();
        }
    }
}
