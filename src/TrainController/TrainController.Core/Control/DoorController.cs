using TrainController.Abstractions.Inputs;

namespace TrainController.Core.Control;

public readonly record struct DoorDecision(bool LeftOpen, bool RightOpen, bool RequestRefused);

/// <summary>
/// Door commands (both modes): the station dwell opens the platform side automatically; the
/// driver may additionally open either side at any time while the train is stopped (e.g. an
/// emergency evacuation). Doors are never commanded open while the train is moving.
/// </summary>
public static class DoorController
{
    public static DoorDecision Decide(
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

        return new DoorDecision(
            stationDwellOpenLeft || driver.LeftDoorsOpenRequested,
            stationDwellOpenRight || driver.RightDoorsOpenRequested,
            false);
    }
}
