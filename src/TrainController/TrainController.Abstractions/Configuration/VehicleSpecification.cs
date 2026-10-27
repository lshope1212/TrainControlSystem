using System.Text.Json.Serialization;
using TrainControl.Common.Utilities;

namespace TrainController.Abstractions.Configuration;

/// <summary>
/// Vehicle data used by the controllers, in SI units. The fleet is the Bombardier
/// FLEXITY 2 in its Blackpool configuration (track data workbook, "General Information":
/// "PAAC will be deploying the Bombardier Flexity 2 ... same configuration as Blackpool").
/// </summary>
/// <remarks>
/// Source: Bombardier FLEXITY 2 Blackpool datasheet (2009). Algorithms must read these
/// values from here rather than repeating the numbers.
/// </remarks>
public sealed record VehicleSpecification
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Datasheet "Maximum speed": 70 km/h ≈ 19.44 m/s ≈ 43.5 mph.</summary>
    public double MaxSpeedMetersPerSecond { get; init; }

    /// <summary>
    /// Datasheet "Medium acceleration (2/3 load) from 0 … 70 km/h". This is a manufacturer
    /// average at 2/3 load, NOT stated as an absolute maximum acceleration.
    /// </summary>
    public double MediumAccelerationMetersPerSecondSquared { get; init; }

    /// <summary>Datasheet "Deceleration (2/3 load) – service brake".</summary>
    public double ServiceBrakeDecelerationMetersPerSecondSquared { get; init; }

    /// <summary>Datasheet "Deceleration (2/3 load) – emergency brake".</summary>
    public double EmergencyBrakeDecelerationMetersPerSecondSquared { get; init; }

    public int MotorCount { get; init; }

    public double MotorRatedPowerWatts { get; init; }

    /// <summary>MotorCount × MotorRatedPowerWatts (480 kW for the FLEXITY 2). Upper bound for power commands.</summary>
    [JsonIgnore]
    public double TotalRatedPowerWatts => MotorCount * MotorRatedPowerWatts;

    public double EmptyMassKilograms { get; init; }

    /// <summary>Datasheet "Car weight (loaded) (4 pass./m²)".</summary>
    public double LoadedMassKilograms { get; init; }

    public double LengthMeters { get; init; }

    /// <summary>Physical door count. Doors are still COMMANDED per side (left/right), not individually.</summary>
    public int DoorCount { get; init; }

    public static VehicleSpecification Flexity2Blackpool { get; } = new VehicleSpecification
    {
        Name = "Bombardier FLEXITY 2 (Blackpool configuration)",
        MaxSpeedMetersPerSecond = UnitConversion.KilometersPerHourToMetersPerSecond(70.0),
        MediumAccelerationMetersPerSecondSquared = 0.5,
        ServiceBrakeDecelerationMetersPerSecondSquared = 1.2,
        EmergencyBrakeDecelerationMetersPerSecondSquared = 2.73,
        MotorCount = 4,
        MotorRatedPowerWatts = UnitConversion.KilowattsToWatts(120.0),
        EmptyMassKilograms = 40_900.0,
        LoadedMassKilograms = 56_700.0,
        LengthMeters = 32.2,
        DoorCount = 8,
    };
}
