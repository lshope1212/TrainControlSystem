using TrainController.Abstractions.Configuration;

namespace TrainController.Integration.Presentation;

/// <summary>
/// Parses and validates the Main UI "Requested speed [ … ] mph" entry. Input boundary only:
/// converts mph text to m/s; never decides what the controller does with it.
/// </summary>
/// <remarks>
/// Rules: must be a finite decimal number; negative is invalid (rejected, nothing applied).
/// Above the vehicle's nominal maximum is ACCEPTED with a warning — the request is only one
/// constraint and the controller caps the effective target at the authorized speed and the
/// vehicle maximum anyway.
/// </remarks>
public sealed record RequestedSpeedEntry(bool IsAccepted, double MetersPerSecond, DisplayValue Message)
{
    public static RequestedSpeedEntry Parse(string? text, VehicleSpecification vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        if (!DisplayUnits.TryParseNumber(text, out var mph))
        {
            return Rejected("Enter a number in mph (e.g. 30.0).");
        }

        if (mph < 0.0)
        {
            return Rejected("Requested speed cannot be negative.");
        }

        var metersPerSecond = DisplayUnits.FromMph(mph);
        var maxMph = DisplayUnits.ToMph(vehicle.MaxSpeedMetersPerSecond);

        if (mph > maxMph)
        {
            return new RequestedSpeedEntry(
                true,
                metersPerSecond,
                new DisplayValue(
                    $"Set to {DisplayUnits.Speed(metersPerSecond)} — above the nominal vehicle maximum ({DisplayUnits.Speed(vehicle.MaxSpeedMetersPerSecond)}); the controller limits the target.",
                    DisplayTone.Warning));
        }

        return new RequestedSpeedEntry(true, metersPerSecond, new DisplayValue($"Set to {DisplayUnits.Speed(metersPerSecond)}.", DisplayTone.Good));
    }

    private static RequestedSpeedEntry Rejected(string message) =>
        new RequestedSpeedEntry(false, 0.0, new DisplayValue(message, DisplayTone.Danger));
}
