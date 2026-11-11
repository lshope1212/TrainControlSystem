using TrainControl.Contracts.Messages;
using TrainModel.Core.Models;
using TrainModel.Core.Services;

namespace TrainControl.Tests.TrainModel;

/// <summary>The Train Model's track-circuit pickup from the Track Model's per-block environments.</summary>
[TestClass]
public class TrackCircuitReceiverTests
{
    private static TrackModelTrainEnvironmentMessage Environment(string trainId, double authority, long sequence, double speed = 12) => new()
    {
        BlockId = "2",
        TrainId = trainId,
        AuthorityMeters = authority,
        CommandedSpeedMetersPerSecond = speed,
        GradePercent = 0.5,
        Sequence = sequence,
    };

    [TestMethod]
    public void AppliesOnlyItsOwnTrainsBlock()
    {
        var receiver = new TrackCircuitReceiver();
        var inputs = new TrainInputs();

        Assert.IsFalse(receiver.TryApply("001", Environment("", 500, 1), inputs), "An empty block is not this train's.");
        Assert.IsFalse(receiver.TryApply("001", Environment("002", 500, 2), inputs));
        Assert.IsTrue(receiver.TryApply("001", Environment("001", 300, 3), inputs));
        Assert.AreEqual(300d, inputs.CommandedAuthorityMeters);
        Assert.AreEqual(12d, inputs.CommandedSpeedMetersPerSecond);
        Assert.AreEqual(0.5, inputs.GradePercent);
    }

    [TestMethod]
    public void AZero_IsAppliedImmediately_AndALateOlderSnapshotCannotRestoreAuthority()
    {
        var receiver = new TrackCircuitReceiver();
        var inputs = new TrainInputs();
        receiver.TryApply("001", Environment("001", 300, 10), inputs);

        Assert.IsTrue(receiver.TryApply("001", Environment("001", 0, 11, speed: 0), inputs), "A stop code is never filtered or held off.");
        Assert.AreEqual(0d, inputs.CommandedAuthorityMeters);
        Assert.AreEqual(0d, inputs.CommandedSpeedMetersPerSecond);

        Assert.IsFalse(receiver.TryApply("001", Environment("001", 300, 10), inputs), "Late (older) snapshot.");
        Assert.IsFalse(receiver.TryApply("001", Environment("001", 300, 11), inputs), "Replayed snapshot.");
        Assert.AreEqual(0d, inputs.CommandedAuthorityMeters);
    }

    [TestMethod]
    public void UnsequencedMessages_AreAppliedInArrivalOrder_AndInvalidValuesAreIgnored()
    {
        var receiver = new TrackCircuitReceiver();
        var inputs = new TrainInputs();
        Assert.IsTrue(receiver.TryApply("001", Environment("001", 300, 0), inputs));
        Assert.IsTrue(receiver.TryApply("001", Environment("001", 200, 0), inputs));
        Assert.AreEqual(200d, inputs.CommandedAuthorityMeters);
        Assert.IsFalse(receiver.TryApply("001", Environment("001", double.NaN, 5), inputs));
        Assert.IsFalse(receiver.TryApply("001", Environment("001", -1, 6), inputs));
        Assert.AreEqual(200d, inputs.CommandedAuthorityMeters);
    }
}
