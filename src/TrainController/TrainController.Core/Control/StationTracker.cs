using TrainControl.Contracts.Enums;
using TrainController.Abstractions.Inputs;

namespace TrainController.Core.Control;

/// <summary>
/// Station-relative distance tracking for ONE train (not absolute track position).
/// </summary>
/// <remarks>
/// Every newly received valid beacon replaces the estimate with the beacon's distance and
/// starts a new station target. Between beacons the estimate decreases by
/// ActualSpeed × DeltaTime. The estimate is SIGNED (slightly negative just past the station);
/// the controller clears the target once the train has departed a served station or gone past
/// it by more than the arrival threshold, so the value never runs away.
/// </remarks>
public sealed class StationTracker
{
    public bool HasEstimate { get; private set; }

    /// <summary>
    /// True only when a station distance derived from a valid beacon is available and usable
    /// (finite, non-negative). Station guidance and the station braking curve apply ONLY then;
    /// missing or invalid station information never implies a zero target.
    /// </summary>
    public bool HasValidDistance =>
        HasEstimate && double.IsFinite(DistanceToNextStationMeters);

    public double DistanceToNextStationMeters { get; private set; }

    public string NextStationName { get; private set; } = string.Empty;

    public PlatformSide PlatformSide { get; private set; } = PlatformSide.None;

    /// <summary>True once the current station target has been served (stop + dwell, or manual arrival).</summary>
    public bool StationServiced { get; private set; }

    public bool IsDwelling { get; private set; }

    public double DwellElapsedSeconds { get; private set; }

    /// <summary>Updates the estimate for one tick. Returns true when a beacon recalibrated it.</summary>
    public bool Update(BeaconData beacon, double actualSpeedMetersPerSecond, double deltaTimeSeconds)
    {
        ArgumentNullException.ThrowIfNull(beacon);

        if (beacon.IsValid && beacon.IsNewlyReceived)
        {
            HasEstimate = true;
            DistanceToNextStationMeters = beacon.DistanceToStationMeters;
            NextStationName = beacon.NextStationName;
            PlatformSide = beacon.PlatformSide;
            StationServiced = false;
            IsDwelling = false;
            DwellElapsedSeconds = 0.0;
            return true;
        }

        if (HasEstimate)
        {
            var travelled = Math.Max(0.0, actualSpeedMetersPerSecond) * deltaTimeSeconds;
            DistanceToNextStationMeters -= travelled;
        }

        return false;
    }

    public void AdvanceDwell(double deltaTimeSeconds)
    {
        IsDwelling = true;
        DwellElapsedSeconds += deltaTimeSeconds;
    }

    public void CompleteService()
    {
        StationServiced = true;
        IsDwelling = false;
        DwellElapsedSeconds = 0.0;
    }

    /// <summary>Drops the current station target (departed or passed); waits for the next beacon.</summary>
    public void Clear() => Reset();

    public void Reset()
    {
        HasEstimate = false;
        DistanceToNextStationMeters = 0.0;
        NextStationName = string.Empty;
        PlatformSide = PlatformSide.None;
        StationServiced = false;
        IsDwelling = false;
        DwellElapsedSeconds = 0.0;
    }
}
