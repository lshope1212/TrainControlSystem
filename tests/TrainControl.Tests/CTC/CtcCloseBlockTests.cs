using CTC.Core.Exceptions;
using CTC.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.CTC;

[TestClass]
public class CtcCloseBlockTests
{
    private static (CTCService Service, FakeMessageSender Sender) CreateService()
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
                    Name = "Green Line",
                    Blocks =
                    {
                        new TrackBlockDefinition { BlockId = "12", BlockNumber = 12 },
                        new TrackBlockDefinition { BlockId = "13", BlockNumber = 13 },
                    },
                },
            },
        });
        return (service, sender);
    }

    [TestMethod]
    public async Task CloseBlockAsync_SendsOneClosedMaintenanceRequestForBlock()
    {
        var (service, sender) = CreateService();

        await service.CloseBlockAsync("12");

        Assert.HasCount(1, sender.SentMessages);
        var message = sender.SentMessages[0] as MaintenanceRequestMessage;
        Assert.IsNotNull(message);
        Assert.AreEqual("12", message.BlockId);
        Assert.AreEqual(MaintenanceState.Closed, message.RequestedState);
    }

    [TestMethod]
    public async Task CloseBlockAsync_OnSuccess_RecordsRequestedStateClosed()
    {
        var (service, _) = CreateService();

        await service.CloseBlockAsync("12");

        Assert.AreEqual(MaintenanceState.Closed, service.State.FindBlock("12")!.RequestedMaintenanceState);
        Assert.AreEqual(MaintenanceState.Open, service.State.FindBlock("13")!.RequestedMaintenanceState);
    }

    [TestMethod]
    public async Task CloseBlockAsync_OnSuccess_DoesNotChangeConfirmedState()
    {
        var (service, _) = CreateService();

        await service.CloseBlockAsync("12");

        Assert.AreEqual(MaintenanceState.Open, service.State.FindBlock("12")!.ConfirmedMaintenanceState);
    }

    [TestMethod]
    public async Task CloseBlockAsync_UnknownBlock_ThrowsAndSendsNothing()
    {
        var (service, sender) = CreateService();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.CloseBlockAsync("99"));

        Assert.IsEmpty(sender.SentMessages);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public async Task CloseBlockAsync_BlankBlockId_ThrowsAndSendsNothing(string blockId)
    {
        var (service, sender) = CreateService();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.CloseBlockAsync(blockId));

        Assert.IsEmpty(sender.SentMessages);
    }

    [TestMethod]
    public async Task CloseBlockAsync_NullBlockId_Throws()
    {
        var (service, _) = CreateService();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.CloseBlockAsync(null!));
    }

    [TestMethod]
    public async Task CloseBlockAsync_WhenSendFails_PropagatesAndLeavesBlockOpen()
    {
        var (service, sender) = CreateService();
        sender.ExceptionToThrow = new MessageSendException("Track Controller is not connected.");

        await Assert.ThrowsExactlyAsync<MessageSendException>(() => service.CloseBlockAsync("12"));

        Assert.AreEqual(MaintenanceState.Open, service.State.FindBlock("12")!.RequestedMaintenanceState);
        Assert.AreEqual(MaintenanceState.Open, service.State.FindBlock("12")!.ConfirmedMaintenanceState);
    }
}
