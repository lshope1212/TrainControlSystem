using CTC.Core.Models;
using CTC.Core.Services;
using TrainControl.Common.Communication;
using TrainControl.Common.Timing;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.CTC;

/// <summary>
/// CTC driven by the shared simulation clock the way the integrated system runs it: the clock
/// ticks every 100 ms of (simulated, deterministic) real time and each state reaches CTC as a
/// serialized <see cref="SystemTimeMessage"/>, which the receiver applies with
/// <see cref="CTCService.SetSystemTimeAsync"/>.
/// </summary>
[TestClass]
public class CtcSharedClockTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private static TimeSpan At(int hours, int minutes, int seconds) => new TimeSpan(hours, minutes, seconds);

    /// <summary>One clock update: tick, then deliver the state to CTC through the real wire format.</summary>
    private static async Task<SystemTimeMessage> TickAndDeliverAsync(SimulationClockService clock, CTCService ctc)
    {
        clock.Tick(Interval);
        return await DeliverAsync(clock, ctc);
    }

    private static async Task<SystemTimeMessage> DeliverAsync(SimulationClockService clock, CTCService ctc)
    {
        var state = clock.Current;
        var sent = new SystemTimeMessage
        {
            SystemTime = state.SystemTime,
            SpeedMultiplier = state.SpeedMultiplier,
            IsPaused = state.IsPaused,
            Sequence = state.Sequence,
        };

        var received = MessageSerializer.DeserializePayload<SystemTimeMessage>(MessageSerializer.Deserialize(MessageSerializer.Serialize(sent)));
        await ctc.SetSystemTimeAsync(received.SystemTime);
        return received;
    }

    private static SimulationClockService RunningClock(double speed)
    {
        var clock = new SimulationClockService();
        clock.SetSpeed(speed);
        clock.Run();
        return clock;
    }

    [TestMethod]
    public async Task AcceleratedClock_DispatchesAtFirstUpdateAtOrPastDeparture()
    {
        var sender = new FakeMessageSender();
        var ctc = BlueLine.CreateService(sender);
        ctc.QueueSchedule([BlueLine.Train("001", At(12, 0, 5), secondsPerBlock: 4)]);

        // 7x: each update is +0.7 s, so 12:00:05 itself is skipped (12:00:04.9 -> 12:00:05.6).
        var clock = RunningClock(7.0);
        SystemTimeMessage message;
        do
        {
            message = await TickAndDeliverAsync(clock, ctc);
            if (message.SystemTime < At(12, 0, 5))
            {
                Assert.IsEmpty(ctc.State.DispatchedTrains, $"Dispatched early at {message.SystemTime}.");
            }
        }
        while (message.SystemTime < At(12, 0, 5));

        Assert.AreEqual(At(12, 0, 5) + TimeSpan.FromMilliseconds(600), message.SystemTime);
        Assert.AreEqual(7.0, message.SpeedMultiplier);
        Assert.IsFalse(message.IsPaused);
        Assert.AreEqual("001", ctc.State.DispatchedTrains.Single().TrainId);
        Assert.IsEmpty(sender.SentMessages, "No movement request until the train is placed on the track.");
        Assert.HasCount(1, sender.DispatchMessages.OfType<TrainDispatchRequestMessage>().Where(m => m.TrainId == "001").ToList(), "Its physical dispatch is sent once.");
    }

    [TestMethod]
    public async Task PausedClock_NeverReachesDeparture_SoNothingIsDispatched()
    {
        var sender = new FakeMessageSender();
        var ctc = BlueLine.CreateService(sender);
        ctc.QueueSchedule([BlueLine.Train("001", At(12, 0, 1), secondsPerBlock: 4)]);
        var clock = new SimulationClockService();

        for (int i = 0; i < 100; i++)
        {
            var message = await TickAndDeliverAsync(clock, ctc);
            Assert.IsTrue(message.IsPaused);
        }

        Assert.AreEqual(At(12, 0, 0), ctc.State.SystemTime);
        Assert.IsEmpty(sender.AllMessages);
        Assert.HasCount(1, ctc.State.DispatchQueue);
    }

    [TestMethod]
    public async Task DwellCompletes_WhenAuthoritativeTimeReachesReadyTime()
    {
        var sender = new FakeMessageSender();
        var layout = BlueLine.CreateLayout();
        layout.Lines[0].Blocks.Single(block => block.BlockId == "A3").StationName = "Station A3";
        var ctc = new CTCService(sender);
        ctc.ApplyTrackLayout(layout);
        BlueLine.ReportAllClear(ctc);
        ctc.QueueSchedule([DwellSchedule()]);

        var clock = RunningClock(10.0);
        clock.SetTime(At(12, 0, 59));
        await DeliverAsync(clock, ctc);
        var train = ctc.State.DispatchedTrains.Single();

        // The train reaches its station stop at 12:00:59 and dwells 45 s, until 12:01:44.
        foreach (var blockId in new[] { "A1", "A2", "A3" })
        {
            ctc.ApplyBlockStatus(new BlockStatusMessage { BlockId = blockId, Occupancy = OccupancyState.Occupied });
        }

        Assert.AreEqual(TrainMovementPhase.Dwelling, train.Phase);
        Assert.AreEqual(At(12, 1, 44), train.ReadyToDepartTime);

        // 10x: +1 s per update. Still dwelling through 12:01:43, departs at 12:01:44.
        while (clock.Current.SystemTime < At(12, 1, 43))
        {
            await TickAndDeliverAsync(clock, ctc);
        }

        Assert.AreEqual(TrainMovementPhase.Dwelling, train.Phase);

        await TickAndDeliverAsync(clock, ctc);
        Assert.AreEqual(At(12, 1, 44), ctc.State.SystemTime);
        Assert.AreEqual(TrainMovementPhase.EnRoute, train.Phase);
        Assert.AreEqual("A5", train.TargetBlockId);
    }

    [TestMethod]
    public async Task ClockSetBackward_DoesNotUndispatch()
    {
        var sender = new FakeMessageSender();
        var ctc = BlueLine.CreateService(sender);
        ctc.QueueSchedule([BlueLine.Train("001", At(12, 0, 1), secondsPerBlock: 4), BlueLine.Train("002", At(12, 30, 0), secondsPerBlock: 4)]);
        var clock = RunningClock(10.0);
        for (int i = 0; i < 3; i++)
        {
            await TickAndDeliverAsync(clock, ctc);
        }

        clock.SetTime(At(11, 0, 0));
        await DeliverAsync(clock, ctc);

        Assert.AreEqual(At(11, 0, 0), ctc.State.SystemTime);
        Assert.AreEqual("001", ctc.State.DispatchedTrains.Single().TrainId);
        Assert.AreEqual("002", ctc.State.DispatchQueue.Single().TrainId);
        Assert.IsEmpty(sender.SentMessages, "No movement request until the train is placed on the track.");
        Assert.AreEqual("001", sender.DispatchMessages.OfType<TrainDispatchRequestMessage>().Single().TrainId, "Only the released train is dispatched, once.");
    }

    /// <summary>Train 001: A1 at 12:00:00, station A3 at 12:01:00 (45 s dwell), A5 at 12:05:00, B10 at 12:10:00.</summary>
    private static ScheduledTrain DwellSchedule()
    {
        var train = new ScheduledTrain { TrainId = "001", LineId = BlueLine.LineId };
        foreach (var blockId in BlueLine.BranchB)
        {
            train.Route.Add(new ScheduledRouteBlock
            {
                BlockId = blockId,
                ArrivalTime = blockId switch
                {
                    "A1" => At(12, 0, 0),
                    "A3" => At(12, 1, 0),
                    "A5" => At(12, 5, 0),
                    "B10" => At(12, 10, 0),
                    _ => null,
                },
                DwellTime = blockId == "A3" ? TimeSpan.FromSeconds(45) : TimeSpan.Zero,
            });
        }

        return train;
    }
}
