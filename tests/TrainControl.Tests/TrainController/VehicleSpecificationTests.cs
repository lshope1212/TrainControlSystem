using TrainController.Abstractions.Configuration;

namespace TrainControl.Tests.TrainController;

[TestClass]
public class VehicleSpecificationTests
{
    [TestMethod]
    public void Flexity2Blackpool_MatchesDatasheet_InSiUnits()
    {
        var v = VehicleSpecification.Flexity2Blackpool;

        Assert.AreEqual(19.444, v.MaxSpeedMetersPerSecond, 1e-3);
        Assert.AreEqual(0.5, v.MediumAccelerationMetersPerSecondSquared);
        Assert.AreEqual(1.2, v.ServiceBrakeDecelerationMetersPerSecondSquared);
        Assert.AreEqual(2.73, v.EmergencyBrakeDecelerationMetersPerSecondSquared);
        Assert.AreEqual(4, v.MotorCount);
        Assert.AreEqual(120_000.0, v.MotorRatedPowerWatts);
        Assert.AreEqual(480_000.0, v.TotalRatedPowerWatts);
        Assert.AreEqual(40_900.0, v.EmptyMassKilograms);
        Assert.AreEqual(56_700.0, v.LoadedMassKilograms);
        Assert.AreEqual(32.2, v.LengthMeters);
        Assert.AreEqual(8, v.DoorCount);
    }

    [TestMethod]
    public void ControllerPolicy_DoesNotInventBrakingMargins()
    {
        Assert.AreEqual(0.0, ControllerPolicy.Default.StationBrakingMarginMeters);
        Assert.AreEqual(0.0, ControllerPolicy.Default.AuthorityBrakingMarginMeters);
    }
}
