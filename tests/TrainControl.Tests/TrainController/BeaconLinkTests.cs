using TrainControl.Contracts.Messages;
using TrainController.Integration;
using TrainController.Integration.Communication;
using TrainController.Integration.Output;

namespace TrainControl.Tests.TrainController;

/// <summary>Beacons from the Train Model: each new READ (sequence) recalibrates the station distance.</summary>
[TestClass]
public class BeaconLinkTests
{
    private sealed class Sink : ITrainModelCommandSink
    {
        public Task SendAsync(TrainControllerCommandMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static TrainModelStatusMessage State(long tick, long beaconSequence, double distance = 300.0) => new TrainModelStatusMessage
    {
        TrainId = "001", TickId = tick, IsActive = true, ActualSpeedMetersPerSecond = 0.0,
        CommandedSpeedMetersPerSecond = 10.0, CommandedAuthorityMeters = 5_000.0, TrackSignalValid = true,
        BeaconValid = true, NextStationName = "Pioneer", DistanceToNextStationMeters = distance,
        PlatformSide = TrainControl.Contracts.Enums.PlatformSide.Left, BeaconSequence = beaconSequence,
    };

    [TestMethod]
    public async Task SameBeaconAgain_WithANewSequence_IsANewRead_ButResendsAreNot()
    {
        var subsystem = TrainControllerSubsystem.Create(trainModelSink: new Sink());
        var receiver = new TrainControllerPipeReceiver(subsystem);
        var slot = subsystem.Registry.Get("TRAIN-001");

        await receiver.HandleStatusAsync(State(1, beaconSequence: 1), CancellationToken.None);
        Assert.AreEqual("Pioneer", slot.LastOutput!.Display.NextStationName);
        Assert.IsTrue(slot.LastOutput.Display.DistanceToNextStationMeters is > 299.0 and <= 300.0);

        // Resend of the same read (same sequence): not a new beacon.
        await receiver.HandleStatusAsync(State(2, beaconSequence: 1), CancellationToken.None);

        // Same station and distance on a later pass (new sequence): recalibrated again.
        await receiver.HandleStatusAsync(State(3, beaconSequence: 2, distance: 300.0), CancellationToken.None);
        Assert.IsTrue(slot.LastOutput!.Display.DistanceToNextStationMeters is > 299.0 and <= 300.0);
    }
}
