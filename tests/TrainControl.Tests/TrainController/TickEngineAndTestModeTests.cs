using TrainControl.Contracts.Messages;
using TrainController.Abstractions.Outputs;
using TrainController.Integration;
using TrainController.Integration.Backends;

namespace TrainControl.Tests.TrainController;

[TestClass]
public class TickEngineAndTestModeTests
{
    private static TrainModelStatusMessage ActiveStatus(string trainId) => new TrainModelStatusMessage
    {
        TrainId = trainId,
        IsActive = true,
        TrackSignalValid = true,
        AuthorizedSpeedMetersPerSecond = 10.0,
        RemainingAuthorityMeters = 10_000.0,
        CabinTemperatureCelsius = 21.0,
    };

    [TestMethod]
    public void TestMode_IsGlobal_AndOffByDefault()
    {
        var rig = new SimulationRig();

        Assert.IsFalse(rig.TestMode.IsTestMode);
        Assert.AreSame(rig.Normal, rig.TestMode.Capture().Provider);

        rig.TestMode.SetTestMode(true);
        var (isTest, provider) = rig.TestMode.Capture();
        Assert.IsTrue(isTest);
        Assert.AreNotSame(rig.Normal, provider, "One provider for all trains; never the Train Model while testing.");
    }

    [TestMethod]
    public async Task NormalAndTestSources_NeverBothDriveTheController()
    {
        var rig = new SimulationRig();
        rig.ActivateTestTrain("TRAIN-001");              // only the Test Model says TRAIN-001 runs
        rig.Normal.Submit(ActiveStatus("TRAIN-003"), beaconNewlyReceived: false, authorityUpdated: false); // only the Train Model says TRAIN-003 runs

        var normal = await rig.Engine.ExecuteTickAsync(CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "TRAIN-003" }, normal.ExecutedTrainIds.ToArray());

        rig.TestMode.SetTestMode(true);
        var test = await rig.Engine.ExecuteTickAsync(CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "TRAIN-001" }, test.ExecutedTrainIds.ToArray());
    }

    [TestMethod]
    public async Task NoTrainRunsByDefault()
    {
        var rig = new SimulationRig();
        rig.TestMode.SetTestMode(true);

        var report = await rig.Engine.ExecuteTickAsync(CancellationToken.None);

        Assert.IsEmpty(report.ExecutedTrainIds);
        Assert.IsEmpty(rig.EvaluatedTrainIds().ToArray());
    }

    [TestMethod]
    public async Task OnlyActiveTrainsRun_InactiveTrainsKeepPendingPresses()
    {
        var rig = new SimulationRig();
        rig.TestMode.SetTestMode(true);
        rig.ActivateTestTrain("TRAIN-001");
        rig.ActivateTestTrain("TRAIN-004");
        rig.Registry.Get("TRAIN-003").Driver.PressEmergencyBrake();

        var report = await rig.Engine.ExecuteTickAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "TRAIN-001", "TRAIN-004" }, report.ExecutedTrainIds.ToArray());
        Assert.IsTrue(rig.Registry.Get("TRAIN-003").Driver.EmergencyBrakePressPending);
    }

    [TestMethod]
    public async Task MainUiDriverAndEngineerInput_ReachTheControllerInTestMode()
    {
        var rig = new SimulationRig();
        rig.TestMode.SetTestMode(true);
        rig.ActivateTestTrain("TRAIN-005", speed: 3.0);
        var slot = rig.Registry.Get("TRAIN-005");
        slot.SetEngineerSettings(1234.0, 5.0);
        slot.Driver.RequestedSpeedMetersPerSecond = 7.5;
        slot.Driver.PressEmergencyBrake();

        await rig.Engine.ExecuteTickAsync(CancellationToken.None);

        var input = rig.Software.Snapshot().Single();
        Assert.AreEqual(1234.0, input.Engineer.Kp);
        Assert.AreEqual(5.0, input.Engineer.Ki);
        Assert.AreEqual(7.5, input.Driver.RequestedSpeedMetersPerSecond);
        Assert.IsTrue(input.Driver.EmergencyBrakeRequested);
        Assert.AreEqual(3.0, input.Model.ActualSpeedMetersPerSecond, "Model input came from the Test Model state.");
        Assert.IsFalse(slot.Driver.EmergencyBrakePressPending, "Press consumed by the tick.");
    }

    [TestMethod]
    public async Task Ticks_UseFixedDeltaTime_AndSimulationTimeIsTickTimesDt()
    {
        var rig = new SimulationRig();
        rig.TestMode.SetTestMode(true);
        rig.ActivateTestTrain("TRAIN-001");

        for (var i = 0; i < 3; i++)
        {
            await rig.Engine.ExecuteTickAsync(CancellationToken.None);
        }

        var inputs = rig.Software.Snapshot();
        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, inputs.Select(x => x.TickId).ToArray());
        foreach (var input in inputs)
        {
            Assert.AreEqual(rig.Options.SimulationTimeStepSeconds, input.DeltaTimeSeconds);
            Assert.AreEqual(input.TickId * rig.Options.SimulationTimeStepSeconds, input.SimulationTimeSeconds, 1e-12);
        }

        Assert.AreEqual(3L, rig.Engine.TickCount);
    }

    [TestMethod]
    public async Task NormalMode_SendsCommandsToTrainModel_TestMode_DoesNot()
    {
        var rig = new SimulationRig();
        rig.Normal.Submit(ActiveStatus("TRAIN-001"), beaconNewlyReceived: false, authorityUpdated: false);
        await rig.Engine.ExecuteTickAsync(CancellationToken.None);
        Assert.HasCount(1, rig.Sink.Sent);
        Assert.AreEqual("TRAIN-001", rig.Sink.Sent[0].TrainId);

        rig.TestMode.SetTestMode(true);
        rig.ActivateTestTrain("TRAIN-001");
        await rig.Engine.ExecuteTickAsync(CancellationToken.None);

        Assert.HasCount(1, rig.Sink.Sent, "Test Mode output must not reach the real Train Model.");
        Assert.AreEqual(2L, rig.Registry.Get("TRAIN-001").LastOutput!.TickId, "Test Mode output is still recorded for the UIs.");
    }

    [TestMethod]
    public async Task NormalProvider_HoldsBeaconReceptionUntilNextTick()
    {
        var rig = new SimulationRig();
        rig.Normal.Submit(ActiveStatus("TRAIN-001"), beaconNewlyReceived: true, authorityUpdated: false);
        rig.Normal.Submit(ActiveStatus("TRAIN-001"), beaconNewlyReceived: false, authorityUpdated: false); // later status before the tick

        await rig.Engine.ExecuteTickAsync(CancellationToken.None);
        await rig.Engine.ExecuteTickAsync(CancellationToken.None);

        var inputs = rig.Software.Snapshot();
        Assert.IsTrue(inputs[0].Model.Beacon.IsNewlyReceived, "Reception submitted between ticks is not lost.");
        Assert.IsFalse(inputs[1].Model.Beacon.IsNewlyReceived, "Reported once.");
    }

    [TestMethod]
    public async Task RealSubsystem_HardwareTrainsFailSafe_SoftwareTrainsRun()
    {
        var subsystem = TrainControllerSubsystem.Create();
        subsystem.TestMode.SetTestMode(true);
        foreach (var id in new[] { "TRAIN-001", "TRAIN-002" })
        {
            var model = subsystem.Registry.Get(id).TestModel;
            model.IsActive = true;
            model.AuthorizedSpeedMetersPerSecond = 10.0;
            model.RemainingAuthorityMeters = 10_000.0;
        }

        await subsystem.Engine.ExecuteTickAsync(CancellationToken.None);

        var software = subsystem.Registry.Get("TRAIN-001").LastOutput!;
        var hardware = subsystem.Registry.Get("TRAIN-002").LastOutput!;
        Assert.IsFalse(software.IsFailSafe);
        Assert.IsTrue(hardware.IsFailSafe);
        Assert.IsTrue(hardware.Commands.EmergencyBrakeCommand);
        Assert.AreEqual(0.0, hardware.Commands.PowerCommandWatts);
        Assert.IsTrue(hardware.Display.EmergencyBrakeCauses.HasFlag(EmergencyBrakeCause.HardwareCommunication));
        Assert.IsInstanceOfType<NotConnectedHardwareBackend>(subsystem.HardwareBackend);
    }

    [TestMethod]
    public async Task NormalProvider_HoldsAuthorityUpdateUntilNextTick()
    {
        var rig = new SimulationRig();
        rig.Normal.Submit(ActiveStatus("TRAIN-001"), beaconNewlyReceived: false, authorityUpdated: true);
        rig.Normal.Submit(ActiveStatus("TRAIN-001"), beaconNewlyReceived: false, authorityUpdated: false);

        await rig.Engine.ExecuteTickAsync(CancellationToken.None);
        await rig.Engine.ExecuteTickAsync(CancellationToken.None);

        var inputs = rig.Software.Snapshot();
        Assert.IsTrue(inputs[0].Model.AuthorityUpdateReceived);
        Assert.IsFalse(inputs[1].Model.AuthorityUpdateReceived);
    }

    [TestMethod]
    public async Task EmergencyBrake_ClearsAfterReset_WhenNoConditionRemains()
    {
        var subsystem = TrainControllerSubsystem.Create();
        subsystem.TestMode.SetTestMode(true);
        var slot = subsystem.Registry.Get("TRAIN-001");
        slot.TestModel.IsActive = true;
        slot.TestModel.ActualSpeedMetersPerSecond = 5.0;
        slot.TestModel.AuthorizedSpeedMetersPerSecond = 10.0;
        slot.TestModel.RemainingAuthorityMeters = 5_000.0;

        // Passenger emergency, then the request is cleared and the WHOLE simulation is reset.
        slot.TestModel.PassengerEmergencyBrakeRequested = true;
        await subsystem.Simulation.StepAsync();
        Assert.IsTrue(slot.LastOutput!.Commands.EmergencyBrakeCommand);

        slot.TestModel.PassengerEmergencyBrakeRequested = false;
        await subsystem.Simulation.ResetAsync();
        await subsystem.Simulation.StepAsync();
        Assert.IsFalse(slot.LastOutput!.Commands.EmergencyBrakeCommand, "Simulation reset clears the latch.");

        // Driver emergency, then a driver reset (train stopped, no other condition).
        slot.Driver.PressEmergencyBrake();
        await subsystem.Simulation.StepAsync();
        Assert.IsTrue(slot.LastOutput!.Commands.EmergencyBrakeCommand);

        slot.TestModel.ActualSpeedMetersPerSecond = 0.0;
        slot.Driver.PressEmergencyBrakeReset();
        await subsystem.Simulation.StepAsync();
        Assert.IsFalse(slot.LastOutput!.Commands.EmergencyBrakeCommand, "Driver reset clears the latch.");
    }

    [TestMethod]
    public async Task EmergencyBrake_ReappearsAfterReset_WhenTheInputStillCausesIt_AndSaysWhy()
    {
        var subsystem = TrainControllerSubsystem.Create();
        subsystem.TestMode.SetTestMode(true);
        var slot = subsystem.Registry.Get("TRAIN-001");
        slot.TestModel.IsActive = true;
        slot.TestModel.ActualSpeedMetersPerSecond = 13.4112;      // 30 mph
        slot.TestModel.AuthorizedSpeedMetersPerSecond = 13.4112;
        slot.TestModel.RemainingAuthorityMeters = 30.48;           // 100 ft < ~108 ft emergency stopping distance

        await subsystem.Simulation.StepAsync();
        await subsystem.Simulation.ResetAsync();                   // keeps the entered inputs by design
        await subsystem.Simulation.StepAsync();

        var display = slot.LastOutput!.Display;
        Assert.IsTrue(slot.LastOutput.Commands.EmergencyBrakeCommand);
        Assert.IsTrue(display.EmergencyBrakeCauses.HasFlag(EmergencyBrakeCause.AuthorityViolation));
        StringAssert.Contains(display.EmergencyBrakeResetBlockedReason, "authority");
    }
}
