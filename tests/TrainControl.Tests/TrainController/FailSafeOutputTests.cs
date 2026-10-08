using TrainController.Abstractions.Configuration;
using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;
using TrainController.Abstractions.Validation;

namespace TrainControl.Tests.TrainController;

[TestClass]
public class FailSafeOutputTests
{
    private static readonly ControllerPolicy Policy = ControllerPolicy.Default;

    /// <summary>A previous output that commanded BOTH doors open, lights on, 20 °C.</summary>
    private static TrainControllerOutput PreviousWithDoorsCommandedOpen() => new TrainControllerOutput
    {
        TrainId = "TRAIN-004",
        TickId = 9,
        Commands = new TrainModelCommand
        {
            PowerCommandWatts = 0.0,
            LeftDoorsOpenCommand = true,
            RightDoorsOpenCommand = true,
            ExteriorLightsCommand = true,
            CabinTemperatureSetpointCelsius = 20.0,
            StationAnnouncement = "Arriving at EDGEBROOK",
        },
        Display = new DriverDisplayState { NextStationName = "EDGEBROOK", DistanceToNextStationMeters = 300.0 },
    };

    private static TrainControllerOutput Create(TrainModelInput? model, TrainControllerOutput? previous = null) =>
        FailSafeOutput.Create("TRAIN-004", 10, EmergencyBrakeCause.HardwareCommunication, "Pi timeout", model, Policy, previous);

    [TestMethod]
    public void FailSafe_IsZeroPowerAndEmergencyBrake_AndPassesValidation()
    {
        var failSafe = Create(new TrainModelInput { ActualSpeedMetersPerSecond = 15.0 }, PreviousWithDoorsCommandedOpen());

        Assert.IsTrue(failSafe.IsFailSafe);
        Assert.AreEqual(0.0, failSafe.Commands.PowerCommandWatts);
        Assert.IsTrue(failSafe.Commands.EmergencyBrakeCommand);
        Assert.AreEqual(string.Empty, failSafe.Commands.StationAnnouncement);
        Assert.IsTrue(failSafe.Display.ControllerFaulted);
        Assert.IsTrue(failSafe.Display.EmergencyBrakeCauses.HasFlag(EmergencyBrakeCause.HardwareCommunication));
        Assert.IsTrue(TrainControllerOutputValidator.Validate(failSafe, "TRAIN-004", 10, VehicleSpecification.Flexity2Blackpool).IsValid);
    }

    [TestMethod]
    public void FailSafe_RetainsLightsCabinAndStationInfo()
    {
        var failSafe = Create(new TrainModelInput(), PreviousWithDoorsCommandedOpen());

        Assert.IsTrue(failSafe.Commands.ExteriorLightsCommand);
        Assert.AreEqual(20.0, failSafe.Commands.CabinTemperatureSetpointCelsius);
        Assert.AreEqual("EDGEBROOK", failSafe.Display.NextStationName);
        Assert.AreEqual(300.0, failSafe.Display.DistanceToNextStationMeters);
    }

    [TestMethod]
    public void FailSafe_WhileMoving_CommandsDoorsClosed_EvenIfPreviouslyCommandedAndPhysicallyOpen()
    {
        var moving = new TrainModelInput { ActualSpeedMetersPerSecond = 5.0, LeftDoorsOpen = true, RightDoorsOpen = true };

        var failSafe = Create(moving, PreviousWithDoorsCommandedOpen());

        Assert.IsFalse(failSafe.Commands.LeftDoorsOpenCommand);
        Assert.IsFalse(failSafe.Commands.RightDoorsOpenCommand);
        Assert.IsTrue(failSafe.Display.DoorInterlockActive);
    }

    [TestMethod]
    public void FailSafe_WithUnknownOrInvalidSpeed_CommandsDoorsClosed()
    {
        var unknown = new TrainModelInput?[]
        {
            null,
            new TrainModelInput { ActualSpeedMetersPerSecond = double.NaN, LeftDoorsOpen = true, RightDoorsOpen = true },
            new TrainModelInput { ActualSpeedMetersPerSecond = -1.0, LeftDoorsOpen = true, RightDoorsOpen = true },
        };

        foreach (var model in unknown)
        {
            var failSafe = Create(model, PreviousWithDoorsCommandedOpen());
            Assert.IsFalse(failSafe.Commands.LeftDoorsOpenCommand);
            Assert.IsFalse(failSafe.Commands.RightDoorsOpenCommand);
        }
    }

    [TestMethod]
    public void FailSafe_WhenStopped_KeepsOnlyPhysicallyOpenDoorsOpen_NeverOpensADoor()
    {
        // Stopped; left door physically open, right physically closed; previous output
        // commanded BOTH open. Only the already-open left door may stay open.
        var stopped = new TrainModelInput { ActualSpeedMetersPerSecond = 0.0, LeftDoorsOpen = true, RightDoorsOpen = false };

        var failSafe = Create(stopped, PreviousWithDoorsCommandedOpen());

        Assert.IsTrue(failSafe.Commands.LeftDoorsOpenCommand);
        Assert.IsFalse(failSafe.Commands.RightDoorsOpenCommand);
        Assert.IsFalse(failSafe.Display.DoorInterlockActive);
    }

    [TestMethod]
    public void FailSafe_StoppedWithDoorsClosed_DoesNotOpenThem()
    {
        var stopped = new TrainModelInput { ActualSpeedMetersPerSecond = 0.0 };

        var failSafe = Create(stopped, PreviousWithDoorsCommandedOpen());

        Assert.IsFalse(failSafe.Commands.LeftDoorsOpenCommand);
        Assert.IsFalse(failSafe.Commands.RightDoorsOpenCommand);
    }

    [TestMethod]
    public void FailSafe_StoppedThreshold_ComesFromPolicy()
    {
        var justAboveThreshold = new TrainModelInput
        {
            ActualSpeedMetersPerSecond = Policy.StoppedSpeedThresholdMetersPerSecond + 0.01,
            LeftDoorsOpen = true,
        };

        Assert.IsFalse(Create(justAboveThreshold).Commands.LeftDoorsOpenCommand);
    }
}
