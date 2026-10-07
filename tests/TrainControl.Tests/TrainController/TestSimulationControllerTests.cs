using TrainController.Abstractions.Outputs;
using TrainController.Integration;
using TrainController.Integration.Backends;
using TrainController.Integration.Simulation;

namespace TrainControl.Tests.TrainController;

[TestClass]
public class TestSimulationControllerTests
{
    /// <summary>Fast wall-clock delay so tests do not wait in real time; records requested intervals.</summary>
    private static (Func<TimeSpan, CancellationToken, Task> Delay, List<TimeSpan> Requested) FastDelay()
    {
        var requested = new List<TimeSpan>();
        Func<TimeSpan, CancellationToken, Task> delay = (interval, token) =>
        {
            lock (requested)
            {
                requested.Add(interval);
            }

            return Task.Delay(1, token);
        };
        return (delay, requested);
    }

    [TestMethod]
    public async Task RunAndStep_RequireTestMode()
    {
        var rig = new SimulationRig();
        rig.ActivateTestTrain("TRAIN-001");

        Assert.IsFalse(rig.Simulation.Run());
        Assert.IsNull(await rig.Simulation.StepAsync());
        Assert.AreEqual(TestSimulationState.Stopped, rig.Simulation.State);
        Assert.AreEqual(0L, rig.Engine.TickCount);
    }

    [TestMethod]
    public async Task Step_ExecutesExactlyOneTickForAllActiveTrains()
    {
        var rig = new SimulationRig();
        rig.TestMode.SetTestMode(true);
        rig.ActivateTestTrain("TRAIN-001");
        rig.ActivateTestTrain("TRAIN-002");
        rig.ActivateTestTrain("TRAIN-009");

        var report = await rig.Simulation.StepAsync();

        Assert.IsNotNull(report);
        Assert.AreEqual(1L, report.TickId);
        Assert.AreEqual(1L, rig.Engine.TickCount);
        CollectionAssert.AreEquivalent(new[] { "TRAIN-001", "TRAIN-002", "TRAIN-009" }, rig.EvaluatedTrainIds().ToArray());
    }

    [TestMethod]
    public async Task Run_TicksContinuously_Stop_FreezesAndPreservesState_RunContinues()
    {
        var (delay, _) = FastDelay();
        var rig = new SimulationRig(delay);
        rig.TestMode.SetTestMode(true);
        rig.ActivateTestTrain("TRAIN-001");
        rig.ActivateTestTrain("TRAIN-004");

        Assert.IsTrue(rig.Simulation.Run());
        Assert.IsFalse(rig.Simulation.Run(), "Already running.");
        await SimulationRig.WaitUntilAsync(() => rig.Engine.TickCount >= 5);

        await rig.Simulation.StopAsync();
        Assert.AreEqual(TestSimulationState.Stopped, rig.Simulation.State);

        var frozenTick = rig.Engine.TickCount;
        var frozenOutput = rig.Registry.Get("TRAIN-004").LastOutput;
        await Task.Delay(50);
        Assert.AreEqual(frozenTick, rig.Engine.TickCount, "No ticks after Stop.");
        Assert.IsNotNull(frozenOutput);
        Assert.AreSame(frozenOutput, rig.Registry.Get("TRAIN-004").LastOutput, "State preserved.");

        // Every active train ran in every completed tick.
        Assert.AreEqual(frozenTick, rig.Software.Snapshot().LongLength);
        Assert.AreEqual(frozenTick, rig.Hardware.Snapshot().LongLength);

        var next = await rig.Simulation.StepAsync();
        Assert.AreEqual(frozenTick + 1, next!.TickId, "Continues from the same state.");
    }

    [TestMethod]
    public async Task Step_IsIgnoredWhileRunning()
    {
        var (delay, _) = FastDelay();
        var rig = new SimulationRig(delay);
        rig.TestMode.SetTestMode(true);
        rig.Simulation.Run();

        Assert.IsNull(await rig.Simulation.StepAsync());

        await rig.Simulation.StopAsync();
    }

    [TestMethod]
    public void SpeedMultiplier_ChangesWallClockInterval_NotSimulationDt()
    {
        var rig = new SimulationRig();
        var dt = rig.Simulation.DeltaTimeSeconds;

        CollectionAssert.AreEqual(new[] { 1, 2, 5, 10 }, rig.Simulation.SupportedSpeedMultipliers.ToArray());
        Assert.AreEqual(1, rig.Simulation.SpeedMultiplier);

        foreach (var multiplier in new[] { 1, 2, 5, 10 })
        {
            rig.Simulation.SetSpeedMultiplier(multiplier);
            Assert.AreEqual(dt / multiplier, rig.Simulation.WallClockTickInterval.TotalSeconds, 1e-9);
            Assert.AreEqual(dt, rig.Simulation.DeltaTimeSeconds, "Simulated timestep never changes.");
        }

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => rig.Simulation.SetSpeedMultiplier(3));
        Assert.AreEqual(10, rig.Simulation.SpeedMultiplier);
    }

    [TestMethod]
    public async Task SpeedMultiplier_AppliesToAllRunningTrains_AndControllersStillGetFixedDt()
    {
        var (delay, requested) = FastDelay();
        var rig = new SimulationRig(delay);
        rig.TestMode.SetTestMode(true);
        rig.ActivateTestTrain("TRAIN-001");
        rig.ActivateTestTrain("TRAIN-002");
        rig.Simulation.SetSpeedMultiplier(10);

        rig.Simulation.Run();
        await SimulationRig.WaitUntilAsync(() => rig.Engine.TickCount >= 5);
        await rig.Simulation.StopAsync();

        var interval = rig.Simulation.WallClockTickInterval;
        TimeSpan[] waits;
        lock (requested)
        {
            waits = requested.ToArray();
        }

        Assert.IsNotEmpty(waits);
        Assert.IsTrue(waits.All(w => w > TimeSpan.Zero && w <= interval), "Waits never exceed timestep / multiplier.");

        foreach (var input in rig.Software.Snapshot().Concat(rig.Hardware.Snapshot()))
        {
            Assert.AreEqual(rig.Options.SimulationTimeStepSeconds, input.DeltaTimeSeconds);
        }

        Assert.HasCount(rig.Software.Snapshot().Length, rig.Hardware.Snapshot(), "Both running trains ticked together.");
    }

    [TestMethod]
    public async Task LeavingTestMode_StopsTheSimulation()
    {
        var (delay, _) = FastDelay();
        var rig = new SimulationRig(delay);
        rig.TestMode.SetTestMode(true);
        rig.Simulation.Run();
        await SimulationRig.WaitUntilAsync(() => rig.Engine.TickCount >= 2);

        rig.TestMode.SetTestMode(false);

        await SimulationRig.WaitUntilAsync(() => rig.Simulation.State == TestSimulationState.Stopped);
    }

    [TestMethod]
    public async Task Reset_ResetsAllTrainRuntime_KeepsEngineerVehicleAndTestInputs()
    {
        var subsystem = TrainControllerSubsystem.Create();
        subsystem.TestMode.SetTestMode(true);

        var t1 = subsystem.Registry.Get("TRAIN-001");
        var t3 = subsystem.Registry.Get("TRAIN-003");
        t1.SetEngineerSettings(500.0, 20.0);
        foreach (var slot in new[] { t1, t3 })
        {
            slot.TestModel.IsActive = true;
            slot.TestModel.ActualSpeedMetersPerSecond = 5.0;
            slot.TestModel.AuthorizedSpeedMetersPerSecond = 10.0;
            slot.TestModel.RemainingAuthorityMeters = 10_000.0;
            slot.Driver.RequestedSpeedMetersPerSecond = 8.0;
        }

        t3.TestModel.PassengerEmergencyBrakeRequested = true;

        await subsystem.Simulation.StepAsync();
        await subsystem.Simulation.StepAsync();

        var software = (SoftwareTrainControllerBackend)subsystem.SoftwareBackend;
        NumericAssert.Positive(software.GetController("TRAIN-001").IntegralMeters);
        Assert.AreEqual(EmergencyBrakeCause.Passenger, software.GetController("TRAIN-003").LatchedEmergencyCauses);

        await subsystem.Simulation.ResetAsync();

        Assert.AreEqual(0L, subsystem.Engine.TickCount);
        Assert.AreEqual(0.0, subsystem.Engine.SimulationTimeSeconds);
        foreach (var slot in subsystem.Registry.Trains)
        {
            Assert.IsNull(slot.LastOutput, $"{slot.TrainId} output cleared.");
        }

        Assert.AreEqual(0.0, software.GetController("TRAIN-001").IntegralMeters);
        Assert.AreEqual(EmergencyBrakeCause.None, software.GetController("TRAIN-003").LatchedEmergencyCauses);

        // Not erased by Reset:
        Assert.AreEqual(500.0, t1.Engineer.Kp);
        Assert.AreEqual(20.0, t1.Engineer.Ki);
        Assert.IsTrue(t1.TestModel.IsActive);
        Assert.AreEqual(5.0, t1.TestModel.ActualSpeedMetersPerSecond);
        Assert.IsTrue(t3.TestModel.PassengerEmergencyBrakeRequested);
        Assert.AreEqual(8.0, t1.Driver.RequestedSpeedMetersPerSecond);

        var afterReset = await subsystem.Simulation.StepAsync();
        Assert.AreEqual(1L, afterReset!.TickId, "Tick counter restarts.");
    }

    [TestMethod]
    public async Task Reset_WhileRunning_StopsFirst()
    {
        var (delay, _) = FastDelay();
        var rig = new SimulationRig(delay);
        rig.TestMode.SetTestMode(true);
        rig.ActivateTestTrain("TRAIN-001");
        rig.Simulation.Run();
        await SimulationRig.WaitUntilAsync(() => rig.Engine.TickCount >= 3);

        await rig.Simulation.ResetAsync();

        Assert.AreEqual(TestSimulationState.Stopped, rig.Simulation.State);
        Assert.AreEqual(0L, rig.Engine.TickCount);
        Assert.AreEqual(1, rig.Software.ResetCount);
        Assert.AreEqual(1, rig.Hardware.ResetCount);
        Assert.IsNull(rig.Registry.Get("TRAIN-001").LastOutput);
    }
}
