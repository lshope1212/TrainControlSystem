using TrainController.Abstractions.Fleet;
using TrainController.Abstractions.Hardware;
using TrainController.Abstractions.Outputs;
using TrainController.Hardware.Pi;
using static TrainControl.Tests.TrainController.ControllerTestInputs;

namespace TrainControl.Tests.TrainController.Hardware;

[TestClass]
public class PiRequestHandlerTests
{
    private static HardwareEnvelope Request(string trainId, long tick, bool passenger = false) => new HardwareEnvelope
    {
        Type = HardwareMessageType.ControllerRequest,
        RequestId = tick,
        TrainId = trainId,
        TickId = tick,
        Input = Input(Model(speed: 5.0, authorized: 10.0, passengerEmergency: passenger), trainId: trainId, tick: tick),
    };

    [TestMethod]
    public void OnePi_ServesExactlyTheFiveHardwareTrains()
    {
        var handler = new HardwareRequestHandler();

        var hello = handler.Handle(new HardwareEnvelope { Type = HardwareMessageType.Hello, RequestId = 1 });

        Assert.AreEqual(HardwareMessageType.HelloResponse, hello.Type);
        Assert.AreEqual(1L, hello.RequestId);
        CollectionAssert.AreEqual(TrainFleet.HardwareTrainIds.ToArray(), hello.ServedTrainIds!.ToArray());
    }

    [TestMethod]
    public void EachRequest_ExecutesOneStep_AndEchoesIdentity()
    {
        var handler = new HardwareRequestHandler();

        var reply = handler.Handle(Request("TRAIN-006", 17));

        Assert.AreEqual(HardwareMessageType.ControllerResponse, reply.Type);
        Assert.AreEqual(HardwareProtocol.Version, reply.ProtocolVersion);
        Assert.AreEqual("TRAIN-006", reply.TrainId);
        Assert.AreEqual(17L, reply.TickId);
        Assert.AreEqual(17L, reply.RequestId);
        Assert.AreEqual("TRAIN-006", reply.Output!.TrainId);
        Assert.AreEqual(17L, reply.Output.TickId);
    }

    [TestMethod]
    public void HardwareTrains_KeepIndependentStateOnOnePi()
    {
        var handler = new HardwareRequestHandler();
        var gains = ArbitraryTestGains;

        handler.Handle(Request("TRAIN-002", 1, passenger: true));
        var t4 = handler.Handle(Request("TRAIN-004", 1) with { Input = Input(Model(speed: 5.0, authorized: 10.0), Automatic(), trainId: "TRAIN-004", tick: 1, engineer: gains) });

        Assert.AreEqual(EmergencyBrakeCause.Passenger, handler.Controller("TRAIN-002").LatchedEmergencyCauses);
        Assert.AreEqual(EmergencyBrakeCause.None, handler.Controller("TRAIN-004").LatchedEmergencyCauses);
        Assert.IsFalse(t4.Output!.Commands.EmergencyBrakeCommand);
        Assert.AreEqual(0.0, handler.Controller("TRAIN-002").IntegralMeters);
        Assert.AreEqual(0.5, handler.Controller("TRAIN-004").IntegralMeters, 1e-9);
        Assert.AreEqual(0.0, handler.Controller("TRAIN-008").IntegralMeters);
    }

    [TestMethod]
    public void InvalidRequests_GetErrorResponses_NeverExceptions()
    {
        var handler = new HardwareRequestHandler();

        var software = handler.Handle(Request("TRAIN-001", 1) with { Input = Input(Model(), trainId: "TRAIN-001", tick: 1) });
        var mismatch = handler.Handle(Request("TRAIN-002", 1) with { TickId = 2 });
        var version = handler.Handle(Request("TRAIN-002", 1) with { ProtocolVersion = 99 });
        var noInput = handler.Handle(Request("TRAIN-002", 1) with { Input = null });
        var wrongType = handler.Handle(new HardwareEnvelope { Type = HardwareMessageType.ControllerResponse });

        foreach (var reply in new[] { software, mismatch, version, noInput, wrongType })
        {
            Assert.AreEqual(HardwareMessageType.ErrorResponse, reply.Type);
            Assert.IsFalse(string.IsNullOrEmpty(reply.Error));
        }
    }

    [TestMethod]
    public void Reset_OneTrainOrAll()
    {
        var handler = new HardwareRequestHandler();
        handler.Handle(Request("TRAIN-002", 1, passenger: true));
        handler.Handle(Request("TRAIN-004", 1, passenger: true));

        handler.Handle(new HardwareEnvelope { Type = HardwareMessageType.ResetRequest, TrainId = "TRAIN-002" });
        Assert.AreEqual(EmergencyBrakeCause.None, handler.Controller("TRAIN-002").LatchedEmergencyCauses);
        Assert.AreEqual(EmergencyBrakeCause.Passenger, handler.Controller("TRAIN-004").LatchedEmergencyCauses);

        var all = handler.Handle(new HardwareEnvelope { Type = HardwareMessageType.ResetRequest, TrainId = string.Empty });
        Assert.AreEqual(HardwareMessageType.ResetResponse, all.Type);
        Assert.AreEqual(EmergencyBrakeCause.None, handler.Controller("TRAIN-004").LatchedEmergencyCauses);
    }
}
