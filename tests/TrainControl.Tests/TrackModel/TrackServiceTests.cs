using TrackModel.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.TrackModel;

[TestClass]
public class TrackServiceTests
{
    private static TrackService CreateService()
    {
        var service = new TrackService();
        service.LoadLayout(SampleTrackLayout.Create());
        return service;
    }

    [TestMethod]
    public void TrainMove_ClearsPreviousBlockAndOccupiesDestination()
    {
        var service = CreateService();
        service.ApplyTrainUpdate(new() { TrainId = "01", CurrentBlockId = "104" });
        service.ApplyTrainUpdate(new() { TrainId = "01", CurrentBlockId = "105", ActualSpeedMetersPerSecond = 10 });
        Assert.IsFalse(service.FindBlock("104")!.IsOccupied);
        Assert.AreEqual(string.Empty, service.FindBlock("104")!.TrainId);
        Assert.AreEqual("01", service.FindBlock("105")!.TrainId);
        Assert.AreEqual(10d, service.FindBlock("105")!.ActualSpeedMetersPerSecond);
    }

    [TestMethod]
    public void Collision_RejectsMoveWithoutChangingExistingOccupancy()
    {
        var service = CreateService();
        service.ApplyTrainUpdate(new() { TrainId = "01", CurrentBlockId = "104" });
        service.ApplyTrainUpdate(new() { TrainId = "02", CurrentBlockId = "105" });
        Assert.Throws<InvalidOperationException>(() => service.ApplyTrainUpdate(new() { TrainId = "01", CurrentBlockId = "105" }));
        Assert.AreEqual("01", service.FindBlock("104")!.TrainId);
        Assert.AreEqual("02", service.FindBlock("105")!.TrainId);
    }

    [TestMethod]
    public void CircuitFailure_ReportsUnknownWithoutErasingPhysicalOccupancy()
    {
        var service = CreateService();
        service.ApplyTrainUpdate(new() { TrainId = "01", CurrentBlockId = "104" });
        service.ApplyFailures(new() { BlockId = "104", TrackCircuitFailure = true });
        Assert.AreEqual(OccupancyState.Unknown, service.CreateBlockState("104").Occupancy);
        Assert.IsTrue(service.FindBlock("104")!.IsOccupied);
        service.ApplyFailures(new() { BlockId = "104" });
        Assert.AreEqual(OccupancyState.Occupied, service.CreateBlockState("104").Occupancy);
    }

    [TestMethod]
    public void PassengerExchange_RetryDoesNotDoubleCountPassengersOrTickets()
    {
        var service = CreateService();
        var message = new TrackModelTrainUpdateMessage { TrainId = "01", CurrentBlockId = "104",
            BoardingPassengers = 12, DisembarkingPassengers = 8, ExchangeId = "exchange-1" };
        service.ApplyTrainUpdate(message);
        service.ApplyTrainUpdate(message);
        Assert.AreEqual(12, service.FindBlock("104")!.WaitingPassengers);
        Assert.AreEqual(12, service.FindBlock("104")!.TicketsSold);
        Assert.AreEqual(8, service.FindBlock("104")!.DisembarkingPassengers);
        Assert.AreEqual(12, service.CreateTicketSales("Blue").TicketsPerHour);
    }

    [TestMethod]
    public void PassengerExchange_TooManyBoardingRejectsWholeUpdate()
    {
        var service = CreateService();
        Assert.Throws<ArgumentException>(() => service.ApplyTrainUpdate(new() { TrainId = "01", CurrentBlockId = "104",
            BoardingPassengers = 25, ExchangeId = "too-many" }));
        Assert.IsFalse(service.FindBlock("104")!.IsOccupied);
        Assert.AreEqual(24, service.FindBlock("104")!.WaitingPassengers);
    }

    [TestMethod]
    public void OccupiedSwitch_RejectsThrowWithoutApplyingOtherCommands()
    {
        var service = CreateService();
        service.ApplyTrainUpdate(new() { TrainId = "01", CurrentBlockId = "103" });
        Assert.Throws<InvalidOperationException>(() => service.ApplyCommand(new() { BlockId = "103",
            Switch = SwitchPosition.Reverse, CommandedSpeedMetersPerSecond = 10 }));
        Assert.AreEqual(SwitchPosition.Normal, service.FindBlock("103")!.Switch);
        Assert.AreEqual(0d, service.FindBlock("103")!.CommandedSpeedMetersPerSecond);
    }

    [TestMethod]
    public void SwitchCommand_ChangesReportedRoute()
    {
        var service = CreateService();
        service.ApplyCommand(new() { BlockId = "103", Switch = SwitchPosition.Reverse });
        Assert.AreEqual("121", service.CreateTrainEnvironment("103").NextBlockId);
    }

    [TestMethod]
    public void InvalidLayout_PreservesLoadedTrack()
    {
        var service = CreateService();
        var invalid = SampleTrackLayout.Create();
        invalid.Blocks[0].ConnectedBlockIds.Add("missing");
        Assert.Throws<ArgumentException>(() => service.LoadLayout(invalid));
        Assert.HasCount(36, service.Layout.Blocks);
        Assert.AreEqual(1, service.LayoutRevision);
    }

    [TestMethod]
    public void NegativeSpeed_RejectsCommand()
    {
        var service = CreateService();
        Assert.Throws<ArgumentException>(() => service.ApplyCommand(new() { BlockId = "104", CommandedSpeedMetersPerSecond = -1 }));
        Assert.AreEqual(0d, service.CreateTrainEnvironment("104").CommandedSpeedMetersPerSecond);
    }

    [TestMethod]
    public void Demo_ProducesExpectedInitialTrainAndFailureState()
    {
        var service = new TrackService();
        SampleTrackLayout.LoadDemo(service);
        Assert.AreEqual("01", service.FindBlock("104")!.TrainId);
        Assert.AreEqual("02", service.FindBlock("116")!.TrainId);
        Assert.IsTrue(service.CreateBlockState("119").TrackCircuitFailure);
        Assert.AreEqual(new TimeSpan(9, 42, 18), service.SystemTime);
    }
}
