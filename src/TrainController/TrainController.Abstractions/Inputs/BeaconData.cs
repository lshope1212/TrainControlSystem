using TrainControl.Contracts.Enums;

namespace TrainController.Abstractions.Inputs;

/// <summary>Most recent beacon information, as part of one tick's model input.</summary>
/// <remarks>
/// <see cref="IsNewlyReceived"/> is true on exactly the tick on which a beacon reception
/// arrives. When it is true and <see cref="IsValid"/> is true, the controller ALWAYS
/// recalibrates its station-distance estimate from <see cref="DistanceToStationMeters"/>.
/// On other ticks the fields are informational and the controller dead-reckons.
/// <para>
/// The flag is set by the model-input provider, not by an external contract: the Test UI
/// adapter sets it when the tester transmits a beacon; the Normal-Mode Train Model adapter
/// will set it from whatever reception signal the Train Model interface eventually defines.
/// </para>
/// </remarks>
public sealed record BeaconData
{
    public bool IsValid { get; init; }

    /// <summary>True only on the tick the beacon was received.</summary>
    public bool IsNewlyReceived { get; init; }

    public string NextStationName { get; init; } = string.Empty;

    /// <summary>Distance from the beacon to the next station when the beacon was read (m).</summary>
    public double DistanceToStationMeters { get; init; }

    public PlatformSide PlatformSide { get; init; } = PlatformSide.None;

    public static BeaconData None { get; } = new BeaconData();
}
