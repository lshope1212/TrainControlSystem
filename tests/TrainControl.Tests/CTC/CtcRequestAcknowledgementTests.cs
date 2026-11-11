using CTC.Core.Models;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.CTC;

/// <summary>
/// Track Controller acknowledgements of CTC's block-control requests: correlated by request ID,
/// distinct from delivery and from the confirmed state.
/// </summary>
[TestClass]
public class CtcRequestAcknowledgementTests
{
    private static RequestAcknowledgementMessage Ack(string type, string requestId, string blockId, RequestAcknowledgementOutcome outcome, string detail) => new RequestAcknowledgementMessage
    {
        RequestType = type,
        RequestId = requestId,
        BlockId = blockId,
        Outcome = outcome,
        Detail = detail,
    };

    [TestMethod]
    public async Task MaintenanceRequests_CarryARequestIdThatIsRemembered()
    {
        var sender = new FakeMessageSender();
        var service = BlueLine.CreateService(sender);

        await service.CloseBlockAsync("A3");

        var request = (MaintenanceRequestMessage)sender.SentMessages.Single();
        Assert.IsFalse(string.IsNullOrWhiteSpace(request.RequestId));
        Assert.AreEqual(request.RequestId, service.State.FindBlock("A3")!.PendingMaintenanceRequestId);
    }

    [TestMethod]
    public async Task RejectedMaintenanceRequest_IsWithdrawnSoTheDispatcherCanActAgain()
    {
        var sender = new FakeMessageSender();
        var service = BlueLine.CreateService(sender);
        await service.CloseBlockAsync("A3");
        var block = service.State.FindBlock("A3")!;
        var changes = new List<CtcStateChangedEventArgs>();
        service.StateChanged += (_, e) => changes.Add(e);

        service.ApplyRequestAcknowledgement(Ack(nameof(MaintenanceRequestMessage), block.PendingMaintenanceRequestId, "A3", RequestAcknowledgementOutcome.Rejected, "Block 'A3' is not owned by HW-TC01."));

        Assert.AreEqual(MaintenanceState.Open, block.RequestedMaintenanceState, "The dead request no longer shows as pending.");
        Assert.AreEqual(MaintenanceState.Open, block.ConfirmedMaintenanceState);
        Assert.AreEqual(string.Empty, block.PendingMaintenanceRequestId);
        var report = changes.Single(change => change.Kind == CtcStateChangeKind.RequestAcknowledged);
        Assert.Contains("rejected the request to close Block A3", report.Message!);
        Assert.Contains("not owned", report.Message!);
    }

    [TestMethod]
    public async Task AcceptedMaintenanceRequest_StaysPendingUntilConfirmed()
    {
        var sender = new FakeMessageSender();
        var service = BlueLine.CreateService(sender);
        await service.CloseBlockAsync("A3");
        var block = service.State.FindBlock("A3")!;

        service.ApplyRequestAcknowledgement(Ack(nameof(MaintenanceRequestMessage), block.PendingMaintenanceRequestId, "A3", RequestAcknowledgementOutcome.AcceptedForEvaluation, "Block A3: Pending closure."));

        // Accepted is not closed: only a block status can confirm that.
        Assert.AreEqual(MaintenanceState.Closed, block.RequestedMaintenanceState);
        Assert.AreEqual(MaintenanceState.Open, block.ConfirmedMaintenanceState);

        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "A3", Occupancy = OccupancyState.Clear, Maintenance = MaintenanceState.Closed });
        Assert.AreEqual(MaintenanceState.Closed, block.ConfirmedMaintenanceState);
    }

    [TestMethod]
    public async Task StaleMaintenanceAcknowledgement_IsIgnored()
    {
        var sender = new FakeMessageSender();
        var service = BlueLine.CreateService(sender);
        await service.CloseBlockAsync("A3");
        var block = service.State.FindBlock("A3")!;

        service.ApplyRequestAcknowledgement(Ack(nameof(MaintenanceRequestMessage), "an-older-request", "A3", RequestAcknowledgementOutcome.Rejected, "late"));
        service.ApplyRequestAcknowledgement(Ack(nameof(MaintenanceRequestMessage), "", "A3", RequestAcknowledgementOutcome.Rejected, "legacy"));

        Assert.AreEqual(MaintenanceState.Closed, block.RequestedMaintenanceState);
        Assert.IsFalse(string.IsNullOrEmpty(block.PendingMaintenanceRequestId));
    }

    [TestMethod]
    public async Task SwitchAcknowledgement_ReportsTheReasonButNeverMovesTheDisplayedSwitch()
    {
        var sender = new FakeMessageSender();
        var service = BlueLine.CreateService(sender);
        service.State.FindBlock("A5")!.HasSwitch = true;
        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "A5", Occupancy = OccupancyState.Clear, Switch = SwitchPosition.Normal });
        await service.SetSwitchPositionAsync("A5", SwitchPosition.Reverse);
        var request = (SwitchPositionRequestMessage)sender.SentMessages.Single();
        var changes = new List<CtcStateChangedEventArgs>();
        service.StateChanged += (_, e) => changes.Add(e);

        service.ApplyRequestAcknowledgement(Ack(nameof(SwitchPositionRequestMessage), request.RequestId, "A5", RequestAcknowledgementOutcome.Rejected, "Switch 5 locked: block 4 is missing CTC reservation feedback."));

        Assert.AreEqual(SwitchPosition.Normal, service.State.FindBlock("A5")!.Switch);
        Assert.Contains("rejected the switch request for Block A5", changes.Single(change => change.Kind == CtcStateChangeKind.RequestAcknowledged).Message!);
    }
}
