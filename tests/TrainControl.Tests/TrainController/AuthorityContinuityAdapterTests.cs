using TrainControl.Contracts.Messages;
using TrainController.Integration;
using TrainController.Integration.Communication;
using TrainController.Integration.Output;

namespace TrainControl.Tests.TrainController;

/// <summary>
/// How the Normal-mode Train Controller (pipe receiver → unchanged safety core) treats the authority
/// the Train Model relays across a block handoff and on a genuine revocation. The core is NOT changed
/// for the handoff: continuity comes from the wayside never sending a false zero.
/// </summary>
[TestClass]
public class AuthorityContinuityAdapterTests
{
    private const string Train = "TRAIN-001";

    private sealed class RecordingSink : ITrainModelCommandSink
    {
        public List<TrainControllerCommandMessage> Sent { get; } = [];

        public Task SendAsync(TrainControllerCommandMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class Link
    {
        private long _tick;

        public RecordingSink Sink { get; } = new();

        public TrainControllerSubsystem Subsystem { get; }

        public TrainControllerPipeReceiver Receiver { get; }

        public Link()
        {
            Subsystem = TrainControllerSubsystem.Create(trainModelSink: Sink);
            Receiver = new TrainControllerPipeReceiver(Subsystem);
        }

        /// <summary>One Train Model status (as TrainModel.Wpf publishes it) and the controller's answer.</summary>
        public async Task<TrainControllerCommandMessage> StatusAsync(double authority, double speed = 10, bool signalValid = true)
        {
            await Receiver.HandleStatusAsync(new TrainModelStatusMessage
            {
                TrainId = Train,
                TickId = ++_tick,
                IsActive = true,
                ActualSpeedMetersPerSecond = speed,
                CommandedSpeedMetersPerSecond = 15,
                CommandedAuthorityMeters = authority,
                TrackSignalValid = signalValid,
            }, CancellationToken.None);
            return Sink.Sent[^1];
        }

        public double Remaining => Subsystem.Registry.Get(Train).LastOutput!.Display.RemainingAuthorityMeters;

        public bool Recalibrated => Subsystem.Registry.Get(Train).LastOutput!.Display.AuthorityRecalibrated;
    }

    [TestMethod]
    public async Task ContinuingAuthorityAcrossTheBoundary_IsRecalibratedFromTheNewBlock_WithoutABrake()
    {
        var link = new Link();
        Assert.IsFalse((await link.StatusAsync(300)).EmergencyBrakeCommand);
        Assert.IsFalse((await link.StatusAsync(300)).EmergencyBrakeCommand);

        // Entering the next block: the same end point from its entry (300 - 50).
        var entered = await link.StatusAsync(250);
        Assert.IsTrue(link.Recalibrated);
        Assert.AreEqual(250d, link.Remaining);
        Assert.IsFalse(entered.EmergencyBrakeCommand);
        Assert.IsFalse(entered.ServiceBrakeCommand);

        // The successor's own grant replaces the continuation with the same value: not a new update,
        // so the estimate keeps counting down instead of jumping back to the block entry.
        var promoted = await link.StatusAsync(250);
        Assert.IsFalse(link.Recalibrated);
        Assert.IsLessThan(250d, link.Remaining);
        Assert.IsFalse(promoted.EmergencyBrakeCommand);
    }

    [TestMethod]
    public async Task AuthoritativeZero_WhileMoving_EngagesTheEmergencyBrakeOnTheNextControlPeriod()
    {
        var link = new Link();
        await link.StatusAsync(300);

        var revoked = await link.StatusAsync(0);

        Assert.IsTrue(revoked.EmergencyBrakeCommand, "No grace period: the first period that sees the zero brakes.");
        Assert.AreEqual(0d, link.Remaining);
        Assert.AreEqual(0d, revoked.PowerCommandWatts);
    }

    [TestMethod]
    public async Task ZeroSentWhileTheTrackSignalIsInvalid_IsAppliedOnTheFirstValidPeriod()
    {
        var link = new Link();
        await link.StatusAsync(300);

        // Signal pickup lost: received values are not trusted, the train is held by the service brake.
        var lost = await link.StatusAsync(0, signalValid: false);
        Assert.IsTrue(lost.ServiceBrakeCommand || lost.EmergencyBrakeCommand, "An invalid signal already stops the train.");
        Assert.IsGreaterThan(0d, link.Remaining, "The untrusted zero is not applied (dead reckoning from the last trusted value).");

        // The signal returns with the zero still coded: it is applied at once, not skipped as "unchanged".
        var restored = await link.StatusAsync(0);
        Assert.IsTrue(link.Recalibrated);
        Assert.AreEqual(0d, link.Remaining);
        Assert.IsTrue(restored.EmergencyBrakeCommand);
    }
}
