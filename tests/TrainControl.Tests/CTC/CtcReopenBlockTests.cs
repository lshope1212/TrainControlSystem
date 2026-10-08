using CTC.Core.Exceptions;
using CTC.Core.Models;
using CTC.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.CTC;

/// <summary>
/// Reopen requests and Track Controller-confirmed maintenance state. CTC's request and the
/// wayside-confirmed state are tracked separately; neither ever overwrites the other.
/// </summary>
[TestClass]
public class CtcReopenBlockTests
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

    /// <summary>Closes block 12 and has the Track Controller confirm it: confirmed and requested Closed.</summary>
    private static async Task<(CTCService Service, FakeMessageSender Sender)> CreateServiceWithClosedBlockAsync()
    {
        var (service, sender) = CreateService();
        await service.CloseBlockAsync("12");
        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "12", Maintenance = MaintenanceState.Closed });
        sender.SentMessages.Clear();
        return (service, sender);
    }

    // ---- ApplyBlockStatus ----

    [TestMethod]
    public void ApplyBlockStatus_MaintenanceClosed_UpdatesConfirmedStateOnly()
    {
        var (service, _) = CreateService();

        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "12", Maintenance = MaintenanceState.Closed });

        var block = service.State.FindBlock("12")!;
        Assert.AreEqual(MaintenanceState.Closed, block.ConfirmedMaintenanceState);
        Assert.AreEqual(MaintenanceState.Open, block.RequestedMaintenanceState);
    }

    [TestMethod]
    public async Task ApplyBlockStatus_MaintenanceOpen_UpdatesConfirmedStateOnly()
    {
        var (service, _) = await CreateServiceWithClosedBlockAsync();

        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "12", Maintenance = MaintenanceState.Open });

        var block = service.State.FindBlock("12")!;
        Assert.AreEqual(MaintenanceState.Open, block.ConfirmedMaintenanceState);
        Assert.AreEqual(MaintenanceState.Closed, block.RequestedMaintenanceState);
    }

    // ---- ReopenBlockAsync ----

    [TestMethod]
    public async Task ReopenBlockAsync_SendsOneOpenMaintenanceRequestForBlock()
    {
        var (service, sender) = await CreateServiceWithClosedBlockAsync();

        await service.ReopenBlockAsync("12");

        Assert.HasCount(1, sender.SentMessages);
        var message = sender.SentMessages[0] as MaintenanceRequestMessage;
        Assert.IsNotNull(message);
        Assert.AreEqual("12", message.BlockId);
        Assert.AreEqual(MaintenanceState.Open, message.RequestedState);
    }

    [TestMethod]
    public async Task ReopenBlockAsync_OnSuccess_RecordsRequestedOpenButStaysConfirmedClosed()
    {
        var (service, _) = await CreateServiceWithClosedBlockAsync();

        await service.ReopenBlockAsync("12");

        var block = service.State.FindBlock("12")!;
        Assert.AreEqual(MaintenanceState.Open, block.RequestedMaintenanceState);
        Assert.AreEqual(MaintenanceState.Closed, block.ConfirmedMaintenanceState);
    }

    [TestMethod]
    public async Task ReopenBlockAsync_WhenSendFails_PropagatesAndLeavesStatesUnchanged()
    {
        var (service, sender) = await CreateServiceWithClosedBlockAsync();
        sender.ExceptionToThrow = new MessageSendException("Track Controller is not connected.");

        await Assert.ThrowsExactlyAsync<MessageSendException>(() => service.ReopenBlockAsync("12"));

        var block = service.State.FindBlock("12")!;
        Assert.AreEqual(MaintenanceState.Closed, block.RequestedMaintenanceState);
        Assert.AreEqual(MaintenanceState.Closed, block.ConfirmedMaintenanceState);
    }

    [TestMethod]
    public async Task ReopenBlockAsync_OnSuccess_RaisesMaintenanceRequestChange()
    {
        var (service, _) = await CreateServiceWithClosedBlockAsync();
        var changes = new List<CtcStateChangedEventArgs>();
        service.StateChanged += (_, e) => changes.Add(e);

        await service.ReopenBlockAsync("12");

        Assert.HasCount(1, changes);
        Assert.AreEqual(CtcStateChangeKind.MaintenanceRequest, changes[0].Kind);
        Assert.AreEqual("12", changes[0].BlockId);
    }

    [TestMethod]
    public async Task ReopenBlockAsync_UnknownBlock_ThrowsAndSendsNothing()
    {
        var (service, sender) = CreateService();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.ReopenBlockAsync("99"));

        Assert.IsEmpty(sender.SentMessages);
    }
}
