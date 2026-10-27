using CTC.Core.Exceptions;
using CTC.Core.Models;
using CTC.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.CTC;

[TestClass]
public class CtcStateChangedTests
{
    private static (CTCService Service, FakeMessageSender Sender, List<CtcStateChangedEventArgs> Changes) CreateService()
    {
        var sender = new FakeMessageSender();
        var service = new CTCService(sender);
        service.ApplyTrackLayout(new TrackLayoutMessage
        {
            Lines =
            {
                new TrackLineDefinition
                {
                    LineId = "GREEN",
                    Blocks = { new TrackBlockDefinition { BlockId = "G12", BlockNumber = 12 } },
                },
            },
        });

        var changes = new List<CtcStateChangedEventArgs>();
        service.StateChanged += (_, e) => changes.Add(e);
        return (service, sender, changes);
    }

    [TestMethod]
    public void ApplyTrackLayout_RaisesTrackLayoutChange()
    {
        var (service, _, changes) = CreateService();

        service.ApplyTrackLayout(new TrackLayoutMessage());

        Assert.HasCount(1, changes);
        Assert.AreEqual(CtcStateChangeKind.TrackLayout, changes[0].Kind);
    }

    [TestMethod]
    public void ApplyBlockStatus_RaisesBlockStatusChangeForBlock()
    {
        var (service, _, changes) = CreateService();

        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "G12", Occupancy = OccupancyState.Occupied });

        Assert.HasCount(1, changes);
        Assert.AreEqual(CtcStateChangeKind.BlockStatus, changes[0].Kind);
        Assert.AreEqual("G12", changes[0].BlockId);
    }

    [TestMethod]
    public void ApplyBlockStatus_UnknownBlock_RaisesNothing()
    {
        var (service, _, changes) = CreateService();

        Assert.ThrowsExactly<ArgumentException>(() => service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "X1" }));

        Assert.IsEmpty(changes);
    }

    [TestMethod]
    public async Task SetSystemTimeAsync_RaisesSystemTimeChange()
    {
        var (service, _, changes) = CreateService();

        await service.SetSystemTimeAsync(TimeSpan.FromHours(7));

        Assert.HasCount(1, changes);
        Assert.AreEqual(CtcStateChangeKind.SystemTime, changes[0].Kind);
    }

    [TestMethod]
    public async Task CloseBlockAsync_OnSuccess_RaisesMaintenanceRequestChange()
    {
        var (service, _, changes) = CreateService();

        await service.CloseBlockAsync("G12");

        Assert.HasCount(1, changes);
        Assert.AreEqual(CtcStateChangeKind.MaintenanceRequest, changes[0].Kind);
        Assert.AreEqual("G12", changes[0].BlockId);
    }

    [TestMethod]
    public async Task CloseBlockAsync_WhenSendFails_RaisesNothing()
    {
        var (service, sender, changes) = CreateService();
        sender.ExceptionToThrow = new MessageSendException("Track Controller is not connected.");

        await Assert.ThrowsExactlyAsync<MessageSendException>(() => service.CloseBlockAsync("G12"));

        Assert.IsEmpty(changes);
    }
}
