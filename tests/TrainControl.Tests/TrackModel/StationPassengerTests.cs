using TrackModel.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.TrackModel;

[TestClass]
public class StationPassengerTests
{
    private static TrackService CreateService()
    {
        var track = new TrackService();
        track.LoadLayout(SampleTrackLayout.Create());
        return track;
    }

    [TestMethod]
    public void ArrivalsSellTicketsEvenWithoutATrain_AndBoardingNeverSellsThemAgain()
    {
        var track = CreateService();
        track.ApplyPassengerDemand(new() { BlockId = "104", WaitingPassengers = 0 });
        track.RecordStationArrivals("104", 30);
        Assert.AreEqual(30, track.CreateTicketSales("Blue").TicketsPerHour);
        Assert.IsFalse(track.FindBlock("104")!.IsOccupied);
        track.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "104" });
        Assert.IsTrue(track.ApplyPassengerExchange(new() { TrainId = "TRAIN-001", Boarded = 20, Alighted = 2, LeftBehind = 10 }));
        var station = track.FindBlock("104")!;
        Assert.AreEqual(10, station.WaitingPassengers);
        Assert.AreEqual(20, station.BoardingPassengers);
        Assert.AreEqual(2, station.DisembarkingPassengers);
        Assert.AreEqual(30, station.TicketsSold);
        Assert.AreEqual(30, track.CreateTicketSales("Blue").TicketsPerHour);
        Assert.AreEqual("001", station.TrainId);
        Assert.IsTrue(station.IsTrainHead);
    }

    [TestMethod]
    public void DemandReplacementAndSeededPopulationDoNotCreateArrivalEvents()
    {
        var track = CreateService();
        track.ApplyPassengerDemand(new() { BlockId = "104", WaitingPassengers = 100 });
        track.ApplyPassengerDemand(new() { BlockId = "104", WaitingPassengers = 100 });
        track.ApplyPassengerDemand(new() { BlockId = "104", WaitingPassengers = 5 });
        Assert.AreEqual(0, track.FindBlock("104")!.TicketsSold);
        Assert.AreEqual(0, track.CreateTicketSales("Blue").TicketsPerHour);
    }

    [TestMethod]
    public void ArrivalsAreAtomicAndAttributedToTheirOwnLine()
    {
        var track = CreateService();
        track.RecordStationArrivals("104", 10);
        track.RecordStationArrivals("G4", 7);
        Assert.AreEqual(10, track.CreateTicketSales("Blue").TicketsPerHour);
        Assert.AreEqual(7, track.CreateTicketSales("Green").TicketsPerHour);
        foreach (var count in new[] { -1, 0, int.MaxValue })
            Assert.Throws<ArgumentException>(() => track.RecordStationArrivals("104", count));
        Assert.Throws<ArgumentException>(() => track.RecordStationArrivals("105", 1));
        Assert.AreEqual(34, track.FindBlock("104")!.WaitingPassengers);
        Assert.AreEqual(10, track.FindBlock("104")!.TicketsSold);
    }

    [TestMethod]
    public void DuplicateUnlabelledReportIsIgnored_ButAnotherExchangeAtTheSameStopNeedsAnId()
    {
        var track = CreateService();
        track.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "104" });
        var report = new PassengerExchangeMessage { TrainId = "001", Boarded = 12, Alighted = 2, LeftBehind = 12 };
        Assert.IsTrue(track.ApplyPassengerExchange(report));
        // New arrivals must remain waiting even if the old exchange is delivered again.
        track.RecordStationArrivals("104", 3);
        report.TrainId = "TRAIN-001";
        Assert.IsFalse(track.ApplyPassengerExchange(report));
        Assert.Throws<InvalidOperationException>(() => track.ApplyPassengerExchange(new() { TrainId = "001", Boarded = 1 }));
        Assert.AreEqual(15, track.FindBlock("104")!.WaitingPassengers);
        Assert.AreEqual(12, track.FindBlock("104")!.BoardingPassengers);
    }

    [TestMethod]
    public void MovingAwayAndReturningStartsAnotherStationVisit()
    {
        var track = CreateService();
        var report = new PassengerExchangeMessage { TrainId = "001", Boarded = 3 };
        track.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "104" });
        track.ApplyPassengerExchange(report);
        track.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "105", ActualSpeedMetersPerSecond = 5 });
        track.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "104" });
        Assert.IsTrue(track.ApplyPassengerExchange(report));
        Assert.AreEqual(18, track.FindBlock("104")!.WaitingPassengers);
        Assert.AreEqual(6, track.FindBlock("104")!.BoardingPassengers);
    }

    [TestMethod]
    public void InvalidExchangeDoesNotChangePassengersOccupancyOrTickets()
    {
        var track = CreateService();
        track.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "104", ActualSpeedMetersPerSecond = 1 });
        Assert.Throws<ArgumentException>(() => track.ApplyPassengerExchange(new() { TrainId = "001", Boarded = 1 }));
        track.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "104" });
        foreach (var report in new[]
        {
            new PassengerExchangeMessage { TrainId = "001", Boarded = -1 },
            new PassengerExchangeMessage { TrainId = "001", Alighted = -1 },
            new PassengerExchangeMessage { TrainId = "001", LeftBehind = -1 },
            new PassengerExchangeMessage { TrainId = "001", Boarded = 25 },
        }) Assert.Throws<ArgumentException>(() => track.ApplyPassengerExchange(report));
        Assert.Throws<InvalidOperationException>(() => track.ApplyPassengerExchange(new() { TrainId = "002", Boarded = 1 }));
        Assert.AreEqual(24, track.FindBlock("104")!.WaitingPassengers);
        Assert.AreEqual("001", track.FindBlock("104")!.TrainId);
        Assert.AreEqual(0, track.FindBlock("104")!.BoardingPassengers);
        Assert.AreEqual(0, track.CreateTicketSales("Blue").TicketsPerHour);
        track.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "105" });
        Assert.Throws<ArgumentException>(() => track.ApplyPassengerExchange(new() { TrainId = "001", Boarded = 1 }));
    }

    [TestMethod]
    public void ExchangeForDispatchedTrainPreservesItsFullFootprintAndPosition()
    {
        var track = new TrackService();
        track.LoadLayout(BlueLineTrackLayout.Create());
        track.RequestTrainPlacement(new() { RequestId = "d1", TrainId = "001", LineId = "Blue", StartBlockId = "10", RouteBlockIds = ["10"] });
        track.ApplyTrainPosition(new() { TrainId = "001", DispatchRequestId = "d1", TrainLengthMeters = 32.2, Sequence = 1 });
        var before = track.FindTrackedTrain("001")!;
        Assert.IsTrue(track.ApplyPassengerExchange(new() { TrainId = "001", Boarded = 4 }));
        var after = track.FindTrackedTrain("001")!;
        Assert.AreEqual(before.FrontPositionMeters, after.FrontPositionMeters);
        CollectionAssert.AreEqual(before.OccupiedBlockIds.ToArray(), after.OccupiedBlockIds.ToArray());
        Assert.AreEqual(20, track.FindBlock("10")!.WaitingPassengers);
    }

    [TestMethod]
    public void BeaconRequiresExplicitStoppingMetadata_AndKeepsLightColorSeparate()
    {
        var layout = BlueLineTrackLayout.Create();
        var beacon = layout.Blocks.Single(b => b.Id == "9");
        var track = new TrackService();
        track.LoadLayout(layout);
        track.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "9" });
        Assert.IsNull(track.CreateTrainBeacon("9"), "Do not invent a stopping point from a station name.");
        beacon.BeaconDistanceToStopMeters = 123.5;
        beacon.BeaconPlatformSide = PlatformSide.Right;
        var message = track.CreateTrainBeacon("9")!;
        Assert.AreEqual("001", message.TrainId);
        Assert.AreEqual(track.FindBlock("10")!.StationName, message.StationName);
        Assert.AreEqual(123.5, message.DistanceToStopMeters);
        Assert.AreEqual(PlatformSide.Right, message.Side);
        Assert.AreEqual(SignalState.Unknown, track.CreateTrainEnvironment("9").Signal);
        track.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "10" });
        Assert.IsNull(track.CreateTrainBeacon("9"));
    }
}
