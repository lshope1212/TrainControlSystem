using TrainControl.Common.Utilities;

namespace TrainControl.Tests.Common;

[TestClass]
public class UnitConversionTests
{
    [TestMethod]
    [DataRow(100.0, 62.137)]
    [DataRow(70.0, 43.496)]
    [DataRow(50.0, 31.069)]
    [DataRow(0.0, 0.0)]
    public void KilometersPerHourToMilesPerHour_Converts(double kilometersPerHour, double expectedMilesPerHour)
    {
        Assert.AreEqual(expectedMilesPerHour, UnitConversion.KilometersPerHourToMilesPerHour(kilometersPerHour), 1e-3);
    }

    [TestMethod]
    [DataRow(100.0, 328.084)]
    [DataRow(50.0, 164.042)]
    [DataRow(0.0, 0.0)]
    public void MetersToFeet_Converts(double meters, double expectedFeet)
    {
        Assert.AreEqual(expectedFeet, UnitConversion.MetersToFeet(meters), 1e-3);
    }
}
