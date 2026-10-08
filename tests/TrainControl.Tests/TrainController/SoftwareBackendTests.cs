using TrainController.Abstractions.Fleet;
using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;
using TrainController.Integration.Backends;
using static TrainControl.Tests.TrainController.ControllerTestInputs;

namespace TrainControl.Tests.TrainController;

[TestClass]
public class SoftwareBackendTests
{
    [TestMethod]
    public void SoftwareBackend_HasOneControllerPerSoftwareTrain_AndNoneForHardwareTrains()
    {
        var backend = new SoftwareTrainControllerBackend();

        Assert.AreEqual(ControllerType.Software, backend.ControllerType);
        foreach (var id in TrainFleet.SoftwareTrainIds)
        {
            Assert.AreEqual(id, backend.GetController(id).TrainId);
        }

        foreach (var id in TrainFleet.HardwareTrainIds)
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => backend.GetController(id));
        }
    }

    [TestMethod]
    public async Task SoftwareBackend_KeepsPerTrainStateIndependent()
    {
        var backend = new SoftwareTrainControllerBackend();
        var gains = EngineerSettings.Create(10.0, 10.0);

        await backend.EvaluateAsync(Input(Model(speed: 5.0, authorized: 10.0), Automatic(), trainId: "TRAIN-001", engineer: gains), CancellationToken.None);
        await backend.EvaluateAsync(Input(Model(speed: 5.0, authorized: 10.0, passengerEmergency: true), Automatic(), trainId: "TRAIN-003", engineer: gains), CancellationToken.None);

        Assert.AreEqual(0.5, backend.GetController("TRAIN-001").IntegralMeters, 1e-9);
        Assert.AreEqual(EmergencyBrakeCause.None, backend.GetController("TRAIN-001").LatchedEmergencyCauses);
        Assert.AreEqual(EmergencyBrakeCause.Passenger, backend.GetController("TRAIN-003").LatchedEmergencyCauses);
        Assert.AreEqual(0.0, backend.GetController("TRAIN-005").IntegralMeters);
        Assert.IsNull(backend.GetController("TRAIN-005").LastOutput);
    }

    [TestMethod]
    public async Task SoftwareBackend_ResetClearsEveryControllerRuntime()
    {
        var backend = new SoftwareTrainControllerBackend();
        await backend.EvaluateAsync(Input(Model(speed: 5.0, passengerEmergency: true), trainId: "TRAIN-007"), CancellationToken.None);

        await backend.ResetAsync(CancellationToken.None);

        Assert.AreEqual(EmergencyBrakeCause.None, backend.GetController("TRAIN-007").LatchedEmergencyCauses);
        Assert.IsNull(backend.GetController("TRAIN-007").LastOutput);
    }
}
