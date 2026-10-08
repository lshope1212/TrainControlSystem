using TrainControl.Common.Utilities;

namespace TrainControl.Tests.Common;

[TestClass]
public class UnitConversionTests
{
    private const double Tolerance = 1e-9;

    [TestMethod]
    public void Speed_RoundTripsAndMatchesKnownValues()
    {
        Assert.AreEqual(43.496, UnitConversion.MetersPerSecondToMilesPerHour(70.0 / 3.6), 1e-3);
        Assert.AreEqual(19.4444, UnitConversion.KilometersPerHourToMetersPerSecond(70.0), 1e-4);
        Assert.AreEqual(12.3, UnitConversion.MetersPerSecondToMilesPerHour(UnitConversion.MilesPerHourToMetersPerSecond(12.3)), Tolerance);
        Assert.AreEqual(70.0, UnitConversion.MetersPerSecondToKilometersPerHour(UnitConversion.KilometersPerHourToMetersPerSecond(70.0)), Tolerance);
    }

    [TestMethod]
    public void Distance_Temperature_Power()
    {
        Assert.AreEqual(1.0, UnitConversion.FeetToMeters(UnitConversion.MetersToFeet(1.0)), Tolerance);
        Assert.AreEqual(3.28084, UnitConversion.MetersToFeet(1.0), 1e-5);
        Assert.AreEqual(32.0, UnitConversion.CelsiusToFahrenheit(0.0), Tolerance);
        Assert.AreEqual(212.0, UnitConversion.CelsiusToFahrenheit(100.0), Tolerance);
        Assert.AreEqual(21.0, UnitConversion.FahrenheitToCelsius(UnitConversion.CelsiusToFahrenheit(21.0)), Tolerance);
        Assert.AreEqual(480.0, UnitConversion.WattsToKilowatts(480_000.0), Tolerance);
    }
}
