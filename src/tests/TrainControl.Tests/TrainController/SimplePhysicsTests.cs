using TrainController.Abstractions.Configuration;
using TrainController.Abstractions.Outputs;
using TrainController.Integration;
using TrainController.Integration.Simulation;

namespace TrainControl.Tests.TrainController;

/// <summary>Test UI "Simple physics" (test tool only; the controllers never compute physics).</summary>
[TestClass]
public class SimplePhysicsTests
{
    private static readonly VehicleSpecification Vehicle = VehicleSpecification.Flexity2Blackpool;

    [TestMethod]
    public void PlantModel_BrakesTractionAndFloor()
    {
        Assert.AreEqual(-Vehicle.EmergencyBrakeDecelerationMetersPerSecondSquared,
            SimplePlantModel.AccelerationMetersPerSecondSquared(10.0, new TrainModelCommand { EmergencyBrakeCommand = true, ServiceBrakeCommand = true }, Vehicle));
        Assert.AreEqual(-Vehicle.ServiceBrakeDecelerationMetersPerSecondSquared,
            SimplePlantModel.AccelerationMetersPerSecondSquared(10.0, new TrainModelCommand { ServiceBrakeCommand = true }, Vehicle));
        Assert.AreEqual(0.0, SimplePlantModel.AccelerationMetersPerSecondSquared(10.0, new TrainModelCommand(), Vehicle));

        // 100 kW at 10 m/s on 40.9 t: 100 000 / (40 900 · 10) ≈ 0.2445 m/s².
        Assert.AreEqual(100_000.0 / (Vehicle.EmptyMassKilograms * 10.0),
            SimplePlantModel.AccelerationMetersPerSecondSquared(10.0, new TrainModelCommand { PowerCommandWatts = 100_000.0 }, Vehicle), 1e-12);

        // From rest the acceleration is capped at the vehicle's medium acceleration.
        Assert.AreEqual(Vehicle.MediumAccelerationMetersPerSecondSquared,
            SimplePlantModel.AccelerationMetersPerSecondSquared(0.0, new TrainModelCommand { PowerCommandWatts = 480_000.0 }, Vehicle), 1e-12);

        Assert.AreEqual(0.0, SimplePlantModel.NextSpeed(0.05, new TrainModelCommand { EmergencyBrakeCommand = true }, Vehicle, 0.1), "Never below 0.");
    }

    [TestMethod]
    public async Task TestMode_WithSimplePhysics_SpeedFollowsCommands_WithoutItSpeedIsHeld()
    {
        foreach (var physics in new[] { true, false })
        {
            var subsystem = TrainControllerSubsystem.Create();
            subsystem.TestMode.SetTestMode(true);
            subsystem.TestMode.SetSimplePhysics(physics);

            var slot = subsystem.Registry.Get("TRAIN-001");
            slot.SetEngineerSettings(20_000.0, 0.0);
            slot.TestModel.IsActive = true;
            slot.TestModel.CommandedSpeedMetersPerSecond = 10.0;
            slot.TestModel.RemainingAuthorityMeters = 10_000.0;

            for (var i = 0; i < 20; i++)
            {
                await subsystem.Simulation.StepAsync();
            }

            if (physics)
            {
                NumericAssert.Positive(slot.TestModel.ActualSpeedMetersPerSecond, "Simple physics accelerates the train.");
                Assert.AreEqual(ControllerFailure.None, slot.LastOutput!.Display.Failures, "Normal run: no false engine failure.");
            }
            else
            {
                Assert.AreEqual(0.0, slot.TestModel.ActualSpeedMetersPerSecond, "Without physics the tester holds the speed.");
                Assert.IsTrue(slot.LastOutput!.Display.Failures.HasFlag(ControllerFailure.Engine),
                    "Held speed with significant power: engine failure is reproduced on purpose.");
            }
        }
    }
}
