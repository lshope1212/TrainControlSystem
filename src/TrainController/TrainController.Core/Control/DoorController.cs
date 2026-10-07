using TrainController.Abstractions.Inputs;

namespace TrainController.Core.Control;

/// <summary>Side-level door commands for one tick.</summary>
public readonly record struct DoorDecision(bool LeftOpen, bool RightOpen, bool RequestRefused);

/// <summary>
/// Door/motion interlock. Doors are commanded per side (left/right), never per physical door.
/// </summary>
/// <remarks>
/// Moving: both sides commanded closed; any driver open request is refused.
/// Stopped, Automatic: doors follow the station dwell (platform side); driver requests are ignored.
/// Stopped, Manual: doors follow the driver's requests.
/// </remarks>
public static class DoorController
{
    public static DoorDecision Decide(
        OperatingMode mode,
        bool stopped,
        bool stationDwellOpenLeft,
        bool stationDwellOpenRight,
        DriverInput driver)
    {
        ArgumentNullException.ThrowIfNull(driver);

        var driverWantsOpen = driver.LeftDoorsOpenRequested || driver.RightDoorsOpenRequested;

        if (!stopped)
        {
            return new DoorDecision(false, false, driverWantsOpen);
        }

        return mode == OperatingMode.Automatic
            ? new DoorDecision(stationDwellOpenLeft, stationDwellOpenRight, false)
            : new DoorDecision(driver.LeftDoorsOpenRequested, driver.RightDoorsOpenRequested, false);
    }
}
