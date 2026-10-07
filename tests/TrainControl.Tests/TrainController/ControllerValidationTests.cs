using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;
using TrainController.Abstractions.Configuration;
using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;
using TrainController.Abstractions.Validation;
using TrainController.Integration.Mapping;

namespace TrainControl.Tests.TrainController;

[TestClass]
public class ControllerValidationTests
{
    private static readonly VehicleSpecification Vehicle = VehicleSpecification.Flexity2Blackpool;

    private static TrainControllerInput ValidInput() => new TrainControllerInput
    {
        TrainId = "TRAIN-001",
        TickId = 1,
        SimulationTimeSeconds = 0.1,
        DeltaTimeSeconds = 0.1,
        Model = new TrainModelInput { IsActive = true, TrackSignalValid = true },
    };

    [TestMethod]
    public void ValidInput_Passes()
    {
        var result = TrainControllerInputValidator.Validate(ValidInput());
        Assert.IsTrue(result.IsValid, result.ToString());
    }

    [TestMethod]
    public void OverspeedActualSpeed_IsAllowed_ForEdgeCaseTesting()
    {
        var input = ValidInput() with { Model = new TrainModelInput { ActualSpeedMetersPerSecond = 40.0 } };
        Assert.IsTrue(TrainControllerInputValidator.Validate(input).IsValid);
    }

    [TestMethod]
    public void InvalidInputs_AreRejected()
    {
        var bad = new[]
        {
            ValidInput() with { TrainId = "TRAIN-011" },
            ValidInput() with { DeltaTimeSeconds = 0.0 },
            ValidInput() with { DeltaTimeSeconds = double.NaN },
            ValidInput() with { TickId = -1 },
            ValidInput() with { Model = new TrainModelInput { ActualSpeedMetersPerSecond = double.NaN } },
            ValidInput() with { Model = new TrainModelInput { ActualSpeedMetersPerSecond = -1.0 } },
            ValidInput() with { Model = new TrainModelInput { RemainingAuthorityMeters = double.PositiveInfinity } },
            ValidInput() with { Model = new TrainModelInput { Beacon = new BeaconData { IsValid = true, DistanceToStationMeters = -3.0 } } },
            ValidInput() with { Engineer = new EngineerSettings { Kp = -1.0 } },
            ValidInput() with { Driver = new DriverInput { RequestedSpeedMetersPerSecond = double.NaN } },
            ValidInput() with { Driver = new DriverInput { AnnouncementRequest = null! } },
        };

        foreach (var input in bad)
        {
            Assert.IsFalse(TrainControllerInputValidator.Validate(input).IsValid, input.ToString());
        }

        Assert.IsFalse(TrainControllerInputValidator.Validate(null).IsValid);
    }

    [TestMethod]
    public void Output_IdentityMismatch_IsRejected()
    {
        var output = new TrainControllerOutput { TrainId = "TRAIN-002", TickId = 5 };

        Assert.IsTrue(TrainControllerOutputValidator.Validate(output, "TRAIN-002", 5, Vehicle).IsValid);
        Assert.IsFalse(TrainControllerOutputValidator.Validate(output, "TRAIN-004", 5, Vehicle).IsValid);
        Assert.IsFalse(TrainControllerOutputValidator.Validate(output, "TRAIN-002", 4, Vehicle).IsValid);
        Assert.IsFalse(TrainControllerOutputValidator.Validate(null, "TRAIN-002", 5, Vehicle).IsValid);
    }

    [TestMethod]
    public void Output_InvalidPower_IsRejected()
    {
        TrainControllerOutput With(TrainModelCommand c) => new TrainControllerOutput { TrainId = "TRAIN-001", TickId = 1, Commands = c };

        var bad = new[]
        {
            new TrainModelCommand { PowerCommandWatts = double.NaN },
            new TrainModelCommand { PowerCommandWatts = double.PositiveInfinity },
            new TrainModelCommand { PowerCommandWatts = -1.0 },
            new TrainModelCommand { PowerCommandWatts = 480_001.0 },
            new TrainModelCommand { PowerCommandWatts = 1000.0, EmergencyBrakeCommand = true },
            new TrainModelCommand { PowerCommandWatts = 1000.0, ServiceBrakeCommand = true },
            new TrainModelCommand { CabinTemperatureSetpointCelsius = double.NaN },
        };

        foreach (var commands in bad)
        {
            Assert.IsFalse(TrainControllerOutputValidator.Validate(With(commands), "TRAIN-001", 1, Vehicle).IsValid, commands.ToString());
        }

        Assert.IsTrue(TrainControllerOutputValidator.Validate(With(new TrainModelCommand { PowerCommandWatts = 480_000.0 }), "TRAIN-001", 1, Vehicle).IsValid);
    }

    [TestMethod]
    public void ContractMapper_CopiesEveryField()
    {
        var message = new TrainModelStatusMessage
        {
            TrainId = "TRAIN-002",
            IsActive = true,
            ActualSpeedMetersPerSecond = 1.5,
            AuthorizedSpeedMetersPerSecond = 12.5,
            RemainingAuthorityMeters = 400.0,
            TrackSignalValid = true,
            BeaconValid = true,
            NextStationName = "WHITED",
            DistanceToNextStationMeters = 150.0,
            PlatformSide = PlatformSide.Both,
            PassengerEmergencyBrakeRequested = true,
            LeftDoorsOpen = true,
            RightDoorsOpen = false,
            ExteriorLightsOn = true,
            CabinTemperatureCelsius = 22.5,
        };

        var input = TrainModelContractMapper.ToModelInput(message, beaconNewlyReceived: true, authorityUpdated: true);

        Assert.IsTrue(input.IsActive);
        Assert.AreEqual(1.5, input.ActualSpeedMetersPerSecond);
        Assert.AreEqual(12.5, input.AuthorizedSpeedMetersPerSecond);
        Assert.AreEqual(400.0, input.RemainingAuthorityMeters);
        Assert.IsTrue(input.AuthorityUpdateReceived);
        Assert.IsTrue(input.TrackSignalValid);
        Assert.AreEqual(new BeaconData { IsValid = true, IsNewlyReceived = true, NextStationName = "WHITED", DistanceToStationMeters = 150.0, PlatformSide = PlatformSide.Both }, input.Beacon);
        Assert.IsTrue(input.PassengerEmergencyBrakeRequested);
        Assert.IsTrue(input.LeftDoorsOpen);
        Assert.IsFalse(input.RightDoorsOpen);
        Assert.IsTrue(input.ExteriorLightsOn);
        Assert.AreEqual(22.5, input.CabinTemperatureCelsius);
        Assert.IsFalse(TrainModelContractMapper.ToModelInput(message, beaconNewlyReceived: false, authorityUpdated: false).Beacon.IsNewlyReceived);

        var command = TrainModelContractMapper.ToCommandMessage(new TrainControllerOutput
        {
            TrainId = "TRAIN-002",
            TickId = 42,
            Commands = new TrainModelCommand
            {
                PowerCommandWatts = 1234.0,
                ServiceBrakeCommand = false,
                EmergencyBrakeCommand = false,
                LeftDoorsOpenCommand = true,
                RightDoorsOpenCommand = true,
                ExteriorLightsCommand = true,
                CabinTemperatureSetpointCelsius = 19.0,
                StationAnnouncement = "Arriving at WHITED",
            },
        });

        Assert.AreEqual("TRAIN-002", command.TrainId);
        Assert.AreEqual(42L, command.TickId);
        Assert.AreEqual(1234.0, command.PowerCommandWatts);
        Assert.IsTrue(command.LeftDoorsOpenCommand);
        Assert.IsTrue(command.RightDoorsOpenCommand);
        Assert.IsTrue(command.ExteriorLightsCommand);
        Assert.AreEqual(19.0, command.CabinTemperatureSetpointCelsius);
        Assert.AreEqual("Arriving at WHITED", command.StationAnnouncement);
    }
}
