using TrainControl.Contracts.Enums;
using TrainController.Abstractions.Configuration;
using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;
using TrainController.Abstractions.Validation;
using TrainController.Core.Services;

namespace TrainControl.Tests.TrainController;

/// <summary>Builders for controller test vectors (SI units).</summary>
internal static class ControllerTestInputs
{
    public const double Dt = 0.1;

    /// <summary>
    /// Arbitrary TEST-ONLY gains so behavior tests do not depend on the untuned Kp = 1 / Ki = 0
    /// startup placeholder. Not tuned values and not a recommendation for operation.
    /// </summary>
    public static readonly EngineerSettings ArbitraryTestGains = EngineerSettings.Create(20_000.0, 0.0);

    public static TrainModelInput Model(
        double speed = 0.0,
        double authorized = 10.0,
        double authority = 10_000.0,
        bool passengerEmergency = false,
        bool leftOpen = false,
        bool rightOpen = false,
        bool signalValid = true,
        BeaconData? beacon = null) => new TrainModelInput
        {
            IsActive = true,
            TrackSignalValid = signalValid,
            ActualSpeedMetersPerSecond = speed,
            AuthorizedSpeedMetersPerSecond = authorized,
            RemainingAuthorityMeters = authority,
            PassengerEmergencyBrakeRequested = passengerEmergency,
            LeftDoorsOpen = leftOpen,
            RightDoorsOpen = rightOpen,
            CabinTemperatureCelsius = 21.0,
            Beacon = beacon ?? BeaconData.None,
        };

    public static BeaconData Beacon(double distance, string station = "PIONEER", PlatformSide side = PlatformSide.Left, bool valid = true, bool isNew = true) =>
        new BeaconData
        {
            IsValid = valid,
            IsNewlyReceived = isNew,
            NextStationName = station,
            DistanceToStationMeters = distance,
            PlatformSide = side,
        };

    public static DriverInput Manual(double requested = 0.0) =>
        new DriverInput { Mode = OperatingMode.Manual, RequestedSpeedMetersPerSecond = requested };

    public static DriverInput Automatic() => new DriverInput { Mode = OperatingMode.Automatic };

    public static TrainControllerInput Input(
        TrainModelInput model,
        DriverInput? driver = null,
        long tick = 1,
        string trainId = "TRAIN-001",
        EngineerSettings? engineer = null,
        ControllerPolicy? policy = null,
        double dt = Dt) => new TrainControllerInput
        {
            TrainId = trainId,
            TickId = tick,
            SimulationTimeSeconds = tick * dt,
            DeltaTimeSeconds = dt,
            Model = model,
            Driver = driver ?? Manual(),
            Engineer = engineer ?? ArbitraryTestGains,
            Vehicle = VehicleSpecification.Flexity2Blackpool,
            Policy = policy ?? ControllerPolicy.Default,
        };

    /// <summary>Steps the controller and asserts the output is valid for this exact train/tick.</summary>
    public static TrainControllerOutput Step(SoftwareTrainController controller, TrainControllerInput input)
    {
        var output = controller.Step(input);
        var validation = TrainControllerOutputValidator.Validate(output, input.TrainId, input.TickId, input.Vehicle);
        Assert.IsTrue(validation.IsValid, $"Invalid controller output at tick {input.TickId}: {validation}");
        return output;
    }
}
