using TrainControl.Common.Communication;
using TrainControl.SystemClock;
using TrainControl.Contracts.Messages;
using TrainController.Integration;
using TrainController.Integration.Communication;
using TrainController.Integration.Output;

namespace TrainControl.Tests.TrainController;

/// <summary>Global tick clock (interim host) and the Normal-mode link to the Train Model.</summary>
[TestClass]
public class SystemClockAndTrainModelLinkTests
{
    private const string Tm = NamedPipeNames.TrainModel;

    private sealed class RecordingTrainModelSink : ITrainModelCommandSink
    {
        public List<TrainControllerCommandMessage> Sent { get; } = new List<TrainControllerCommandMessage>();

        public Task SendAsync(TrainControllerCommandMessage message, CancellationToken cancellationToken)
        {
            lock (Sent) Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBus
    {
        public List<(string Pipe, object Message)> Sent { get; } = new List<(string, object)>();
        public TickClockCoordinator? Clock { get; set; }
        public bool AutoAnswer { get; set; } = true;

        public Task SendAsync(string pipe, object message, CancellationToken ct)
        {
            lock (Sent) Sent.Add((pipe, message));
            if (AutoAnswer && Clock is not null)
            {
                if (message is TickMessage t) Clock.Handle(new TickDoneMessage { TickId = t.TickId, Participant = pipe });
                if (message is ResetMessage r) Clock.Handle(new ResetDoneMessage { ResetId = r.ResetId, Participant = pipe });
            }

            return Task.CompletedTask;
        }

        public List<T> Of<T>() { lock (Sent) return Sent.Select(s => s.Message).OfType<T>().ToList(); }
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Condition not reached in time.");
            await Task.Delay(10);
        }
    }

    [TestMethod]
    public async Task Clock_StepsOneTickAtATime_WaitingForTickDone()
    {
        var bus = new FakeBus();
        await using var clock = new TickClockCoordinator(new TickClockOptions(new[] { Tm }), bus.SendAsync);
        bus.Clock = clock;
        clock.Start();

        clock.Step();
        await Until(() => clock.State.TickId == 1);
        clock.Step();
        await Until(() => clock.State.TickId == 2);

        CollectionAssert.AreEqual(new long[] { 1, 2 }, bus.Of<TickMessage>().Select(t => t.TickId).ToArray());
        Assert.AreEqual(0.2, bus.Of<TickMessage>()[1].SimulationTimeSeconds, 1e-9);
        Assert.IsFalse(clock.State.IsRunning, "Step leaves the clock paused.");
    }

    [TestMethod]
    public async Task Clock_MissingTickDone_StopsWithFault_NeverSkipsATick()
    {
        var bus = new FakeBus { AutoAnswer = false };
        await using var clock = new TickClockCoordinator(new TickClockOptions(new[] { Tm }, TickTimeout: TimeSpan.FromMilliseconds(150)), bus.SendAsync);
        bus.Clock = clock;
        clock.Start();

        clock.Run();
        await Until(() => clock.State.Fault.Length > 0);

        Assert.IsFalse(clock.State.IsRunning);
        StringAssert.Contains(clock.State.Fault, Tm);
        Assert.HasCount(1, bus.Of<TickMessage>(), "Tick 2 is never sent without TickDone(1).");
    }

    [TestMethod]
    public async Task Clock_Reset_WaitsForResetDone_ThenRestartsFromTickOne()
    {
        var bus = new FakeBus();
        await using var clock = new TickClockCoordinator(new TickClockOptions(new[] { Tm }), bus.SendAsync);
        bus.Clock = clock;
        var localReset = false;
        clock.LocalReset += _ => { localReset = true; return Task.CompletedTask; };
        clock.Start();

        clock.Step();
        await Until(() => clock.State.TickId == 1);
        clock.Reset();
        await Until(() => bus.Of<ResetMessage>().Count == 1 && clock.State.TickId == 0);
        clock.Step();
        await Until(() => clock.State.TickId == 1 && bus.Of<TickMessage>().Count == 2);

        Assert.IsTrue(localReset);
        Assert.AreEqual(1L, bus.Of<TickMessage>()[1].TickId);
    }

    [TestMethod]
    public async Task Clock_ControlRequestFromParticipant_IsApplied()
    {
        var bus = new FakeBus();
        await using var clock = new TickClockCoordinator(new TickClockOptions(new[] { Tm }), bus.SendAsync);
        bus.Clock = clock;
        clock.Start();

        clock.Handle(new ClockControlMessage { Command = ClockCommand.SetSpeed, SpeedMultiplier = 5, Requester = Tm });
        clock.Handle(new ClockControlMessage { Command = ClockCommand.Run, Requester = Tm });
        await Until(() => clock.State.TickId >= 3);
        clock.Handle(new ClockControlMessage { Command = ClockCommand.Pause, Requester = Tm });

        Assert.AreEqual(5.0, clock.State.SpeedMultiplier);
        await Until(() => !clock.State.IsRunning);
        Assert.IsTrue(bus.Of<ClockStateMessage>().Any(s => s.IsRunning), "State changes are broadcast.");
    }

    [TestMethod]
    public async Task TrainModelState_IsAnsweredWithCommandsForTheSameTick_AndDoorsFollowCommands()
    {
        var sink = new RecordingTrainModelSink();
        var subsystem = TrainControllerSubsystem.Create(trainModelSink: sink);
        var receiver = new TrainControllerPipeReceiver(subsystem);
        TrainModelStatusMessage State(long tick) => new TrainModelStatusMessage
        {
            TrainId = "TRAIN-001", TickId = tick, IsActive = true, ActualSpeedMetersPerSecond = 0.0,
            CommandedSpeedMetersPerSecond = 10.0, CommandedAuthorityMeters = 2_000.0, TrackSignalValid = true,
        };

        await receiver.HandleAsync(MessageSerializer.Deserialize(MessageSerializer.Serialize(State(41))), CancellationToken.None);
        await receiver.FlushAsync(); // states from the pipe are computed on the train's worker
        await receiver.HandleStatusAsync(State(42), CancellationToken.None);

        Assert.HasCount(2, sink.Sent);
        Assert.AreEqual(41L, sink.Sent[0].TickId);
        Assert.AreEqual(42L, sink.Sent[1].TickId);
        var last = subsystem.Registry.Get("TRAIN-001").LastOutput!;
        Assert.IsFalse(last.Display.AuthorityRecalibrated, "Same authority value: counted down, not recalibrated.");
        Assert.AreEqual(42L, subsystem.Engine.TickCount);
    }

    [TestMethod]
    public async Task InTestMode_TrainModelStateDoesNotRunTheController()
    {
        var sink = new RecordingTrainModelSink();
        var subsystem = TrainControllerSubsystem.Create(trainModelSink: sink);
        subsystem.TestMode.SetTestMode(true);

        await new TrainControllerPipeReceiver(subsystem).HandleStatusAsync(
            new TrainModelStatusMessage { TrainId = "TRAIN-001", TickId = 7, IsActive = true, TrackSignalValid = true }, CancellationToken.None);

        Assert.IsEmpty(sink.Sent);
    }

    private static TrainModelStatusMessage Canonical(long tick, string id = "001") => new TrainModelStatusMessage
    {
        TrainId = id, TickId = tick, IsActive = true, ActualSpeedMetersPerSecond = 5.0,
        CommandedSpeedMetersPerSecond = 10.0, CommandedAuthorityMeters = 2_000.0, TrackSignalValid = true,
    };

    [TestMethod]
    public async Task CanonicalId_IsMappedToTheFleet_AndDuplicateTicksAreIgnored()
    {
        var sink = new RecordingTrainModelSink();
        var subsystem = TrainControllerSubsystem.Create(trainModelSink: sink);
        var receiver = new TrainControllerPipeReceiver(subsystem);

        await receiver.HandleStatusAsync(Canonical(10), CancellationToken.None);
        await receiver.HandleStatusAsync(Canonical(10), CancellationToken.None); // duplicate
        await receiver.HandleStatusAsync(Canonical(13), CancellationToken.None); // 3 steps later
        await receiver.HandleStatusAsync(Canonical(5, "077"), CancellationToken.None); // not in the fleet

        Assert.HasCount(2, sink.Sent);
        Assert.AreEqual("TRAIN-001", sink.Sent[1].TrainId);
        Assert.AreEqual(13L, sink.Sent[1].TickId);
    }

    [TestMethod]
    public async Task TickIdGoingBackwards_IsTreatedAsAReset()
    {
        var sink = new RecordingTrainModelSink();
        var subsystem = TrainControllerSubsystem.Create(trainModelSink: sink);
        var receiver = new TrainControllerPipeReceiver(subsystem);
        subsystem.Registry.Get("TRAIN-001").Driver.PressEmergencyBrake();

        await receiver.HandleStatusAsync(Canonical(50), CancellationToken.None);
        Assert.IsTrue(subsystem.Registry.Get("TRAIN-001").LastOutput!.Commands.EmergencyBrakeCommand);

        await receiver.HandleStatusAsync(Canonical(1), CancellationToken.None);
        Assert.IsFalse(subsystem.Registry.Get("TRAIN-001").LastOutput!.Display.EmergencyBrakeLatched, "Reset clears the latch.");
    }

    [TestMethod]
    public async Task TrafficLight_FromTheTrackModel_IsApplied()
    {
        var subsystem = TrainControllerSubsystem.Create(trainModelSink: new RecordingTrainModelSink());
        var receiver = new TrainControllerPipeReceiver(subsystem);

        await receiver.HandleAsync(MessageSerializer.Deserialize(MessageSerializer.Serialize(
            new TrackModelSignalMessage { TrainId = "001", BlockId = "12", Signal = TrainControl.Contracts.Enums.SignalState.Yellow })), CancellationToken.None);
        await receiver.HandleStatusAsync(Canonical(1), CancellationToken.None);

        Assert.AreEqual(TrainControl.Contracts.Enums.SignalState.Yellow, subsystem.Registry.Get("TRAIN-001").LastOutput!.Display.TrafficLight);
    }
}
