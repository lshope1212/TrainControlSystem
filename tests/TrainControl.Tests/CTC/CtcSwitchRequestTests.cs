using CTC.Core.Exceptions;
using CTC.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.CTC;

[TestClass]
public class CtcSwitchRequestTests
{
    private static (CTCService Service, FakeMessageSender Sender) CreateService(SwitchPosition reportedSwitch = SwitchPosition.Normal)
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
                        new TrackBlockDefinition { BlockId = "G1", BlockNumber = 1 },
                        new TrackBlockDefinition { BlockId = "G2", BlockNumber = 2, HasSwitch = true },
                    },
                },
            },
        });
        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "G2", Switch = reportedSwitch });
        return (service, sender);
    }

    [TestMethod]
    [DataRow(SwitchPosition.Normal, SwitchPosition.Reverse)]
    [DataRow(SwitchPosition.Reverse, SwitchPosition.Normal)]
    public async Task SetSwitchPositionAsync_SendsOneRequestForBlock(SwitchPosition reported, SwitchPosition requested)
    {
        var (service, sender) = CreateService(reported);

        await service.SetSwitchPositionAsync("G2", requested);

        Assert.HasCount(1, sender.SentMessages);
        var message = sender.SentMessages[0] as SwitchPositionRequestMessage;
        Assert.IsNotNull(message);
        Assert.AreEqual("G2", message.BlockId);
        Assert.AreEqual(requested, message.RequestedPosition);
    }

    [TestMethod]
    public async Task SetSwitchPositionAsync_OnSuccess_LeavesReportedSwitchUnchanged()
    {
        var (service, _) = CreateService(SwitchPosition.Normal);

        await service.SetSwitchPositionAsync("G2", SwitchPosition.Reverse);

        // Only a Track Controller BlockStatusMessage may change the reported position.
        Assert.AreEqual(SwitchPosition.Normal, service.State.FindBlock("G2")!.Switch);
    }

    [TestMethod]
    public async Task SetSwitchPositionAsync_ReportedStatusAfterRequest_UpdatesSwitch()
    {
        var (service, _) = CreateService(SwitchPosition.Normal);

        await service.SetSwitchPositionAsync("G2", SwitchPosition.Reverse);
        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "G2", Switch = SwitchPosition.Reverse });

        Assert.AreEqual(SwitchPosition.Reverse, service.State.FindBlock("G2")!.Switch);
    }

    [TestMethod]
    public async Task SetSwitchPositionAsync_BlockWithoutSwitch_ThrowsAndSendsNothing()
    {
        var (service, sender) = CreateService();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.SetSwitchPositionAsync("G1", SwitchPosition.Reverse));

        Assert.IsEmpty(sender.SentMessages);
    }

    [TestMethod]
    public async Task SetSwitchPositionAsync_UnknownPosition_ThrowsAndSendsNothing()
    {
        var (service, sender) = CreateService();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.SetSwitchPositionAsync("G2", SwitchPosition.Unknown));

        Assert.IsEmpty(sender.SentMessages);
    }

    [TestMethod]
    public async Task SetSwitchPositionAsync_UnknownBlock_ThrowsAndSendsNothing()
    {
        var (service, sender) = CreateService();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.SetSwitchPositionAsync("G99", SwitchPosition.Reverse));

        Assert.IsEmpty(sender.SentMessages);
    }

    [TestMethod]
    public async Task SetSwitchPositionAsync_WhenSendFails_PropagatesAndLeavesSwitchUnchanged()
    {
        var (service, sender) = CreateService(SwitchPosition.Normal);
        sender.ExceptionToThrow = new MessageSendException("Track Controller is not connected.");

        await Assert.ThrowsExactlyAsync<MessageSendException>(() => service.SetSwitchPositionAsync("G2", SwitchPosition.Reverse));

        Assert.AreEqual(SwitchPosition.Normal, service.State.FindBlock("G2")!.Switch);
    }
}
