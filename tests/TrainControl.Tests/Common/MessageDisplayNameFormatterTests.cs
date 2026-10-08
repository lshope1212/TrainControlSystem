using TrainControl.Common.Communication;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.Common;

[TestClass]
public class MessageDisplayNameFormatterTests
{
    [TestMethod]
    [DataRow(nameof(AuthorityMessage), "Authority")]
    [DataRow(nameof(BlockStatusMessage), "Block status")]
    [DataRow(nameof(MaintenanceRequestMessage), "Maintenance request")]
    [DataRow(nameof(MovementRequestMessage), "Movement request")]
    [DataRow(nameof(MovementSuggestionMessage), "Movement suggestion")]
    [DataRow(nameof(SpeedCommandMessage), "Speed command")]
    [DataRow(nameof(SwitchPositionRequestMessage), "Switch position request")]
    [DataRow(nameof(SystemTimeMessage), "System time")]
    [DataRow(nameof(TicketSalesMessage), "Ticket sales")]
    [DataRow(nameof(TrackLayoutMessage), "Track layout")]
    [DataRow(nameof(TrackStateMessage), "Track state")]
    [DataRow(nameof(TrainAuthorizationStatusMessage), "Train authorization status")]
    [DataRow(nameof(TrainStateMessage), "Train state")]
    public void ToDisplayName_KnownContract_ReturnsMappedName(string messageType, string expected)
    {
        Assert.AreEqual(expected, MessageDisplayNameFormatter.ToDisplayName(messageType));
    }

    [TestMethod]
    [DataRow("SomeFutureStatusMessage", "Some future status")]
    [DataRow("CTCHeartbeatMessage", "CTC heartbeat")]
    [DataRow("Heartbeat", "Heartbeat")]
    [DataRow("Message", "Message")]
    public void ToDisplayName_UnknownType_FallsBackToSentenceCase(string messageType, string expected)
    {
        Assert.AreEqual(expected, MessageDisplayNameFormatter.ToDisplayName(messageType));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void ToDisplayName_Blank_ReturnsPlaceholder(string messageType)
    {
        Assert.AreEqual("Unknown message", MessageDisplayNameFormatter.ToDisplayName(messageType));
    }
}
