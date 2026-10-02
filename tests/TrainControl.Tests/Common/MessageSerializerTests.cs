using System.Text.Json;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.Common;

[TestClass]
public class MessageSerializerTests
{
    [TestMethod]
    public void Serialize_BlockStatusMessage_UsesContractNameAsMessageType()
    {
        var json = MessageSerializer.Serialize(new BlockStatusMessage { BlockId = "G12" });

        using var document = JsonDocument.Parse(json);
        Assert.AreEqual("BlockStatusMessage", document.RootElement.GetProperty("messageType").GetString());
    }

    [TestMethod]
    public void Serialize_WritesEnumsAsStrings()
    {
        var json = MessageSerializer.Serialize(new BlockStatusMessage
        {
            BlockId = "G12",
            Occupancy = OccupancyState.Occupied,
            Signal = SignalState.Red,
        });

        using var document = JsonDocument.Parse(json);
        var payload = document.RootElement.GetProperty("payload");
        Assert.AreEqual("Occupied", payload.GetProperty("occupancy").GetString());
        Assert.AreEqual("Red", payload.GetProperty("signal").GetString());
    }

    [TestMethod]
    public void Serialize_IsSingleLine()
    {
        var json = MessageSerializer.Serialize(new BlockStatusMessage { BlockId = "G12" });

        Assert.DoesNotContain("\n", json);
    }

    [TestMethod]
    public void RoundTrip_BlockStatusMessage_PreservesAllFields()
    {
        var original = new BlockStatusMessage
        {
            BlockId = "G12",
            Occupancy = OccupancyState.Occupied,
            Signal = SignalState.Red,
            Switch = SwitchPosition.Normal,
            Crossing = CrossingState.Open,
        };

        var envelope = MessageSerializer.Deserialize(MessageSerializer.Serialize(original));
        var message = MessageSerializer.DeserializePayload<BlockStatusMessage>(envelope);

        Assert.AreEqual("BlockStatusMessage", envelope.MessageType);
        Assert.AreEqual(original.BlockId, message.BlockId);
        Assert.AreEqual(original.Occupancy, message.Occupancy);
        Assert.AreEqual(original.Signal, message.Signal);
        Assert.AreEqual(original.Switch, message.Switch);
        Assert.AreEqual(original.Crossing, message.Crossing);
    }

    [TestMethod]
    public void RoundTrip_MaintenanceRequestMessage_PreservesBlockIdAndRequestedState()
    {
        var json = MessageSerializer.Serialize(new MaintenanceRequestMessage
        {
            BlockId = "G12",
            RequestedState = MaintenanceState.Closed,
        });

        var envelope = MessageSerializer.Deserialize(json);
        var message = MessageSerializer.DeserializePayload<MaintenanceRequestMessage>(envelope);

        Assert.AreEqual("MaintenanceRequestMessage", envelope.MessageType);
        Assert.AreEqual("G12", message.BlockId);
        Assert.AreEqual(MaintenanceState.Closed, message.RequestedState);
        Assert.Contains("\"requestedState\":\"Closed\"", json);
    }

    [TestMethod]
    public void RoundTrip_SystemTimeMessage_PreservesTime()
    {
        var time = new TimeSpan(7, 30, 15);

        var envelope = MessageSerializer.Deserialize(MessageSerializer.Serialize(new SystemTimeMessage { SystemTime = time }));

        Assert.AreEqual(time, MessageSerializer.DeserializePayload<SystemTimeMessage>(envelope).SystemTime);
    }

    [TestMethod]
    public void RoundTrip_TrackLayoutMessage_PreservesNestedBlocks()
    {
        var original = new TrackLayoutMessage
        {
            Lines =
            {
                new TrackLineDefinition
                {
                    LineId = "GREEN",
                    Blocks = { new TrackBlockDefinition { BlockId = "G1", ConnectedBlockIds = { "G2" } } },
                },
            },
        };

        var envelope = MessageSerializer.Deserialize(MessageSerializer.Serialize(original));
        var message = MessageSerializer.DeserializePayload<TrackLayoutMessage>(envelope);

        Assert.HasCount(1, message.Lines);
        Assert.AreEqual("G1", message.Lines[0].Blocks[0].BlockId);
        CollectionAssert.AreEqual(new[] { "G2" }, message.Lines[0].Blocks[0].ConnectedBlockIds);
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("{}")]
    [DataRow("{\"messageType\":\"BlockStatusMessage\"}")]
    public void Deserialize_MalformedEnvelope_ThrowsJsonException(string json)
    {
        Assert.ThrowsExactly<JsonException>(() => MessageSerializer.Deserialize(json));
    }
}
