using TrainController.Abstractions.Fleet;
using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;
using TrainController.Integration.Backends;
using TrainController.Integration.Execution;
using TrainController.Integration.Logging;
using TrainController.Integration.Routing;
using static TrainControl.Tests.TrainController.ControllerTestInputs;

namespace TrainControl.Tests.TrainController;

[TestClass]
public class ExecutionServiceTests
{
    private static (TrainControllerExecutionService Service, FakeTrainControllerBackend Software, FakeTrainControllerBackend Hardware, InMemoryTrainControllerEventLog Log) Build()
    {
        var software = new FakeTrainControllerBackend(ControllerType.Software);
        var hardware = new FakeTrainControllerBackend(ControllerType.Hardware);
        var log = new InMemoryTrainControllerEventLog();
        return (new TrainControllerExecutionService(new TrainControllerRouter(software, hardware), log), software, hardware, log);
    }

    [TestMethod]
    public async Task SoftwareTrain_IsExecutedBySoftwareBackendOnly()
    {
        var (service, software, hardware, _) = Build();

        var output = await service.ExecuteAsync(Input(Model(), trainId: "TRAIN-005"), null, CancellationToken.None);

        Assert.IsFalse(output.IsFailSafe);
        Assert.HasCount(1, software.Evaluated);
        Assert.IsEmpty(hardware.Evaluated);
    }

    [TestMethod]
    public async Task HardwareTrain_IsExecutedByHardwareBackendOnly()
    {
        var (service, software, hardware, _) = Build();

        await service.ExecuteAsync(Input(Model(), trainId: "TRAIN-006"), null, CancellationToken.None);

        Assert.HasCount(1, hardware.Evaluated);
        Assert.IsEmpty(software.Evaluated);
    }

    [TestMethod]
    public async Task HardwareUnavailable_FailsSafe_WithNoSoftwareFallback()
    {
        var software = new FakeTrainControllerBackend(ControllerType.Software);
        var service = new TrainControllerExecutionService(new TrainControllerRouter(software, new NotConnectedHardwareBackend()));

        var output = await service.ExecuteAsync(Input(Model(speed: 8.0), trainId: "TRAIN-002", tick: 3), null, CancellationToken.None);

        Assert.IsTrue(output.IsFailSafe);
        Assert.AreEqual("TRAIN-002", output.TrainId);
        Assert.AreEqual(3L, output.TickId);
        Assert.AreEqual(0.0, output.Commands.PowerCommandWatts);
        Assert.IsTrue(output.Commands.EmergencyBrakeCommand);
        Assert.IsTrue(output.Display.EmergencyBrakeCauses.HasFlag(EmergencyBrakeCause.HardwareCommunication));
        Assert.IsTrue(output.Display.ControllerFaulted);
        Assert.IsEmpty(software.Evaluated, "No automatic Software fallback.");
    }

    [TestMethod]
    public async Task InvalidInput_FailsSafe_WithoutCallingAnyBackend()
    {
        var (service, software, hardware, _) = Build();

        var output = await service.ExecuteAsync(Input(Model(speed: double.NaN)), null, CancellationToken.None);

        Assert.IsTrue(output.IsFailSafe);
        Assert.IsTrue(output.Display.EmergencyBrakeCauses.HasFlag(EmergencyBrakeCause.InvalidInput));
        Assert.IsEmpty(software.Evaluated);
        Assert.IsEmpty(hardware.Evaluated);
    }

    [TestMethod]
    public async Task BackendException_FailsSafe()
    {
        var (service, software, _, _) = Build();
        software.Behavior = _ => throw new InvalidOperationException("boom");

        var output = await service.ExecuteAsync(Input(Model()), null, CancellationToken.None);

        Assert.IsTrue(output.IsFailSafe);
        Assert.IsTrue(output.Display.EmergencyBrakeCauses.HasFlag(EmergencyBrakeCause.ControllerFault));
    }

    [TestMethod]
    public async Task MismatchedOrInvalidBackendOutput_FailsSafe()
    {
        var bad = new Func<TrainControllerInput, TrainControllerOutput>[]
        {
            i => new TrainControllerOutput { TrainId = "TRAIN-004", TickId = i.TickId },                  // wrong train
            i => new TrainControllerOutput { TrainId = i.TrainId, TickId = i.TickId - 1 },                 // stale tick
            i => new TrainControllerOutput { TrainId = i.TrainId, TickId = i.TickId, Commands = new TrainModelCommand { PowerCommandWatts = double.NaN } },
            i => new TrainControllerOutput { TrainId = i.TrainId, TickId = i.TickId, Commands = new TrainModelCommand { PowerCommandWatts = 10.0, EmergencyBrakeCommand = true } },
        };

        foreach (var behavior in bad)
        {
            var (service, _, hardware, _) = Build();
            hardware.Behavior = behavior;

            var output = await service.ExecuteAsync(Input(Model(), trainId: "TRAIN-002", tick: 5), null, CancellationToken.None);

            Assert.IsTrue(output.IsFailSafe);
            Assert.AreEqual("TRAIN-002", output.TrainId);
            Assert.AreEqual(5L, output.TickId);
            Assert.IsTrue(output.Display.EmergencyBrakeCauses.HasFlag(EmergencyBrakeCause.HardwareCommunication));
        }
    }

    [TestMethod]
    public async Task Routing_IsLoggedOncePerTrain()
    {
        var (service, _, _, log) = Build();

        for (var tick = 1; tick <= 3; tick++)
        {
            await service.ExecuteAsync(Input(Model(), trainId: "TRAIN-001", tick: tick), null, CancellationToken.None);
            await service.ExecuteAsync(Input(Model(), trainId: "TRAIN-002", tick: tick), null, CancellationToken.None);
        }

        var routing = log.Snapshot().Where(e => e.Category == "Routing").ToArray();
        Assert.HasCount(2, routing);
        Assert.IsTrue(routing.Any(e => e.TrainId == "TRAIN-001" && e.Message.Contains("Software")));
        Assert.IsTrue(routing.Any(e => e.TrainId == "TRAIN-002" && e.Message.Contains("Hardware")));
    }

    [TestMethod]
    public async Task SafetyEvents_AreLogged()
    {
        var log = new InMemoryTrainControllerEventLog();
        var service = new TrainControllerExecutionService(
            new TrainControllerRouter(new SoftwareTrainControllerBackend(), new NotConnectedHardwareBackend()), log);

        var first = await service.ExecuteAsync(Input(Model(speed: 5.0, passengerEmergency: true, beacon: Beacon(400.0)), tick: 1), null, CancellationToken.None);
        await service.ExecuteAsync(Input(Model(speed: 0.0, passengerEmergency: true), new DriverInput { EmergencyBrakeResetRequested = true }, tick: 2), first, CancellationToken.None);

        var messages = log.Snapshot().Select(e => e.Message).ToArray();
        Assert.IsTrue(messages.Any(m => m.Contains("Passenger emergency brake")));
        Assert.IsTrue(messages.Any(m => m.Contains("New beacon")));
        Assert.IsTrue(messages.Any(m => m.Contains("reset rejected")));
        Assert.IsTrue(messages.Any(m => m.Contains("Automatic announcement")));
    }
}
