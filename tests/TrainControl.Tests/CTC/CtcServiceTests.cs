using CTC.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.CTC;

[TestClass]
public class CtcServiceTests
{
    private static TrackLayoutMessage CreateLayout() => new TrackLayoutMessage
    {
        Lines =
        {
            new TrackLineDefinition
            {
                LineId = "GREEN",
                Name = "Green Line",
                Blocks =
                {
                    new TrackBlockDefinition
                    {
                        BlockId = "G1",
                        BlockNumber = 1,
                        Section = "A",
                        LengthMeters = 100.0,
                        HasSignal = true,
                        ConnectedBlockIds = { "G2" },
                    },
                    new TrackBlockDefinition
                    {
                        BlockId = "G2",
                        BlockNumber = 2,
                        Section = "A",
                        LengthMeters = 150.0,
                        StationName = "Pioneer",
                        HasSwitch = true,
                        HasCrossing = true,
                        ConnectedBlockIds = { "G1", "G3" },
                    },
                },
            },
            new TrackLineDefinition
            {
                LineId = "RED",
                Name = "Red Line",
                Blocks = { new TrackBlockDefinition { BlockId = "R1", BlockNumber = 1 } },
            },
        },
    };

    private static CTCService CreateServiceWithLayout()
    {
        var service = new CTCService(new FakeMessageSender());
        service.ApplyTrackLayout(CreateLayout());
        return service;
    }

    [TestMethod]
    public void NewService_StartsWithEmptyState()
    {
        var service = new CTCService(new FakeMessageSender());

        Assert.IsEmpty(service.State.Lines);
        Assert.IsEmpty(service.State.ScheduledTrains);
        Assert.IsEmpty(service.State.DispatchQueue);
        Assert.IsEmpty(service.State.DispatchedTrains);
        Assert.AreEqual(TimeSpan.Zero, service.State.SystemTime);
    }

    [TestMethod]
    public void ApplyTrackLayout_CreatesLinesAndBlocks()
    {
        var service = CreateServiceWithLayout();

        Assert.HasCount(2, service.State.Lines);

        var green = service.State.FindLine("GREEN");
        Assert.IsNotNull(green);
        Assert.AreEqual("Green Line", green.Name);
        Assert.HasCount(2, green.Blocks);

        var block = service.State.FindBlock("G2");
        Assert.IsNotNull(block);
        Assert.AreEqual(2, block.BlockNumber);
        Assert.AreEqual("A", block.Section);
        Assert.AreEqual(150.0, block.LengthMeters);
        Assert.AreEqual("Pioneer", block.StationName);
        Assert.IsTrue(block.HasSwitch);
        Assert.IsFalse(block.HasSignal);
        Assert.IsTrue(block.HasCrossing);
        CollectionAssert.AreEqual(new[] { "G1", "G3" }, block.ConnectedBlockIds.ToArray());
        Assert.AreEqual(OccupancyState.Unknown, block.Occupancy);
        Assert.AreEqual(MaintenanceState.Open, block.RequestedMaintenanceState);
    }

    [TestMethod]
    public void ApplyTrackLayout_ReplacesPreviousLayout()
    {
        var service = CreateServiceWithLayout();

        service.ApplyTrackLayout(new TrackLayoutMessage
        {
            Lines = { new TrackLineDefinition { LineId = "BLUE", Name = "Blue Line" } },
        });

        Assert.HasCount(1, service.State.Lines);
        Assert.IsNull(service.State.FindLine("GREEN"));
    }

    [TestMethod]
    public void ApplyBlockStatus_UpdatesMatchingBlock()
    {
        var service = CreateServiceWithLayout();

        service.ApplyBlockStatus(new BlockStatusMessage
        {
            BlockId = "G2",
            Occupancy = OccupancyState.Occupied,
            Signal = SignalState.Red,
            Switch = SwitchPosition.Reverse,
            Crossing = CrossingState.Closed,
        });

        var block = service.State.FindBlock("G2")!;
        Assert.AreEqual(OccupancyState.Occupied, block.Occupancy);
        Assert.AreEqual(SignalState.Red, block.Signal);
        Assert.AreEqual(SwitchPosition.Reverse, block.Switch);
        Assert.AreEqual(CrossingState.Closed, block.Crossing);

        Assert.AreEqual(OccupancyState.Unknown, service.State.FindBlock("G1")!.Occupancy);
    }

    [TestMethod]
    public void ApplyBlockStatus_UnknownBlock_Throws()
    {
        var service = CreateServiceWithLayout();

        Assert.ThrowsExactly<ArgumentException>(
            () => service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "X99" }));
    }

    [TestMethod]
    public void ApplyTicketSales_UpdatesCorrectLine()
    {
        var service = CreateServiceWithLayout();

        service.ApplyTicketSales(new TicketSalesMessage { LineId = "RED", TicketsPerHour = 250 });

        Assert.AreEqual(250, service.State.FindLine("RED")!.TicketSalesPerHour);
        Assert.AreEqual(0, service.State.FindLine("GREEN")!.TicketSalesPerHour);
    }

    [TestMethod]
    public void ApplyTrainAuthorization_StoresSiValuesOnMatchingTrain()
    {
        var service = new CTCService(new FakeMessageSender());

        service.ApplyTrainAuthorization(new TrainAuthorizationStatusMessage
        {
            TrainId = "T1",
            AuthorizedSpeedMetersPerSecond = 12.5,
            AuthorizedAuthorityMeters = 400.0,
        });
        service.ApplyTrainAuthorization(new TrainAuthorizationStatusMessage
        {
            TrainId = "T2",
            AuthorizedSpeedMetersPerSecond = 5.0,
            AuthorizedAuthorityMeters = 50.0,
        });
        service.ApplyTrainAuthorization(new TrainAuthorizationStatusMessage
        {
            TrainId = "T1",
            AuthorizedSpeedMetersPerSecond = 15.0,
            AuthorizedAuthorityMeters = 600.0,
        });

        Assert.HasCount(2, service.State.DispatchedTrains);

        var train = service.State.FindDispatchedTrain("T1")!;
        Assert.AreEqual(15.0, train.AuthorizedSpeedMetersPerSecond);
        Assert.AreEqual(600.0, train.AuthorizedAuthorityMeters);
        Assert.AreEqual(string.Empty, train.CurrentBlockId);

        Assert.AreEqual(5.0, service.State.FindDispatchedTrain("T2")!.AuthorizedSpeedMetersPerSecond);
    }

    [TestMethod]
    public void CreateMaintenanceRequest_ReturnsMessageWithoutChangingState()
    {
        var service = CreateServiceWithLayout();

        var message = service.CreateMaintenanceRequest("G1", MaintenanceState.Closed);

        Assert.AreEqual("G1", message.BlockId);
        Assert.AreEqual(MaintenanceState.Closed, message.RequestedState);
        Assert.AreEqual(MaintenanceState.Open, service.State.FindBlock("G1")!.RequestedMaintenanceState);
    }

    [TestMethod]
    public void CreateSwitchPositionRequest_ReturnsMessage()
    {
        var service = CreateServiceWithLayout();

        var message = service.CreateSwitchPositionRequest("G2", SwitchPosition.Reverse);

        Assert.AreEqual("G2", message.BlockId);
        Assert.AreEqual(SwitchPosition.Reverse, message.RequestedPosition);
    }

    [TestMethod]
    public void CreateSwitchPositionRequest_BlockWithoutSwitch_Throws()
    {
        var service = CreateServiceWithLayout();

        Assert.ThrowsExactly<ArgumentException>(
            () => service.CreateSwitchPositionRequest("G1", SwitchPosition.Normal));
    }

    [TestMethod]
    public void CreateMovementRequest_ReturnsReleaseTrainMessage()
    {
        var service = new CTCService(new FakeMessageSender());

        var message = service.CreateMovementRequest("T1");

        Assert.AreEqual("T1", message.TrainId);
        Assert.AreEqual(MovementRequestType.ReleaseTrain, message.RequestType);
    }

    [TestMethod]
    public void CreateMovementRequest_BlankTrainId_Throws()
    {
        var service = new CTCService(new FakeMessageSender());

        Assert.ThrowsExactly<ArgumentException>(() => service.CreateMovementRequest(" "));
    }

    [TestMethod]
    public void SetSystemTime_UpdatesStateWithoutTimer()
    {
        var service = new CTCService(new FakeMessageSender());
        var time = new TimeSpan(7, 30, 15);

        service.SetSystemTime(time);

        Assert.AreEqual(time, service.State.SystemTime);
    }
}
