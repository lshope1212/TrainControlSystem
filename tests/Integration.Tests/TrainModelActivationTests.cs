using System.Collections.Concurrent;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Messages;

namespace TrainControl.Integration.Tests;

/// <summary>
/// The production Train Model's side of physical dispatch, over its real pipes: a
/// <see cref="TrainActivationMessage"/> creates and activates the train at cumulative position 0 and an
/// initial position report goes to the Track Model at once (even with the clock paused); afterwards every
/// physics tick reports the cumulative position with an increasing sequence; a retried activation never
/// resets an operating train. The Track Model is stood in for by a listener on its pipe.
/// </summary>
[TestClass]
public sealed class TrainModelActivationTests
{
    private sealed class TrackModelStandIn : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _listen;

        public TrackModelStandIn(string pipe) =>
            _listen = NamedPipeTransport.ListenAsync(pipe, envelope =>
            {
                if (envelope.MessageType == nameof(TrainStateMessage)) Reports.Enqueue(MessageSerializer.DeserializePayload<TrainStateMessage>(envelope));
                return Task.CompletedTask;
            }, _ => { }, _stop.Token);

        public ConcurrentQueue<TrainStateMessage> Reports { get; } = new();

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _listen;
            _stop.Dispose();
        }
    }

    private static TrainActivationMessage Activation(string dispatch, bool activate = true) => new()
    {
        DispatchRequestId = dispatch,
        TrainId = "001",
        LineId = "Green",
        StartBlockId = "GREEN-63",
        Activate = activate,
    };

    [TestMethod]
    public async Task Activation_CreatesTheTrainAtZero_AndReportsAtOnce_EvenWithThePausedClock()
    {
        await using var system = new LiveSystem();
        await using var trackModel = new TrackModelStandIn(system.TrackModelPipe);
        await using var trainModel = await LiveTrainModel.StartAsync(system);

        await NamedPipeTransport.SendAsync(system.TrainModelPipe, Activation("d1"));

        await LiveSystem.UntilAsync(() => Task.FromResult(!trackModel.Reports.IsEmpty), "the initial position report reaches the Track Model.", TimeSpan.FromSeconds(5));
        var initial = trackModel.Reports.First();
        Assert.AreEqual("001", initial.TrainId);
        Assert.AreEqual(0d, initial.PositionMeters);
        Assert.AreEqual(0d, initial.SpeedMetersPerSecond);
        Assert.AreEqual(32.2, initial.TrainLengthMeters, 1e-9, "From the train's TrainSpecs.");
        Assert.AreEqual("d1", initial.DispatchRequestId);
        var state = (await trainModel.StateAsync("001"))!;
        Assert.AreEqual("Green Line", state.LineName);
        CollectionAssert.AreEqual(new[] { "001" }, await trainModel.RosterAsync());
    }

    [TestMethod]
    public async Task EveryPhysicsTick_ReportsTheCumulativePosition_AndARetriedActivationNeverResetsTheTrain()
    {
        await using var system = new LiveSystem();
        await using var trackModel = new TrackModelStandIn(system.TrackModelPipe);
        await using var trainModel = await LiveTrainModel.StartAsync(system);
        await NamedPipeTransport.SendAsync(system.TrainModelPipe, Activation("d1"));
        await using var clock = new ClockPump(new TimeSpan(9, 0, 0), 2, system.TrainModelPipe);

        // A Train Controller command with traction (what the real controller sends).
        await NamedPipeTransport.SendAsync(system.TrainModelPipe, new TrainControllerCommandMessage { TrainId = "001", TickId = 1, PowerCommandWatts = 120_000, CabinTemperatureSetpointCelsius = 21 });
        await LiveSystem.UntilAsync(async () => (await trainModel.StateAsync("001"))!.PositionMeters > 5, "physics moves the train.", TimeSpan.FromSeconds(15));

        // Retries of the same dispatch and a different dispatch: neither resets the operating train.
        double before = (await trainModel.StateAsync("001"))!.PositionMeters;
        await NamedPipeTransport.SendAsync(system.TrainModelPipe, Activation("d1"));
        await NamedPipeTransport.SendAsync(system.TrainModelPipe, Activation("d2"));
        await Task.Delay(500);
        double after = (await trainModel.StateAsync("001"))!.PositionMeters;
        Assert.IsGreaterThanOrEqualTo(before, after, "Never reset to 0.");
        CollectionAssert.AreEqual(new[] { "001" }, await trainModel.RosterAsync());

        var reports = trackModel.Reports.ToArray();
        Assert.IsTrue(reports.All(report => report.DispatchRequestId == "d1"), "Still the original dispatch.");
        for (int i = 1; i < reports.Length; i++)
        {
            Assert.IsGreaterThanOrEqualTo(reports[i - 1].Sequence, reports[i].Sequence, "Ordered by physics tick.");
            Assert.IsGreaterThanOrEqualTo(reports[i - 1].PositionMeters - 1e-9, reports[i].PositionMeters, "The cumulative position only grows here.");
        }

        Assert.IsGreaterThan(10, reports.Length, "One report per physics tick (coalesced only if the receiver is slower).");

        // Retirement: the train stops being in service and stops reporting.
        await NamedPipeTransport.SendAsync(system.TrainModelPipe, Activation("d1", activate: false));
        await Task.Delay(500);
        int count = trackModel.Reports.Count;
        await Task.Delay(700);
        Assert.AreEqual(count, trackModel.Reports.Count, "A retired train sends no more position reports.");
    }
}
