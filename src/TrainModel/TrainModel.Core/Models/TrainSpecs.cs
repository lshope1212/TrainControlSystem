namespace TrainModel.Core.Models;

/// <summary>
/// Physical specification of the train consist. Authoritative values come from the
/// Bombardier FLEXITY 2 (Blackpool) datasheet. ALL UNITS METRIC (internal SI) —
/// conversion to imperial happens only at the UI boundary.
/// </summary>
public class TrainSpecs
{
    /// <summary>Number of cars/modules in the consist.</summary>
    public int Consist { get; set; } = 5;

    /// <summary>Total consist length, meters. FLEXITY 2: 32.2 m.</summary>
    public double LengthMeters { get; set; } = 32.2;

    /// <summary>Consist height, meters. FLEXITY 2: 3.42 m.</summary>
    public double HeightMeters { get; set; } = 3.42;

    /// <summary>Consist width, meters. FLEXITY 2: 2.65 m.</summary>
    public double WidthMeters { get; set; } = 2.65;

    /// <summary>Empty consist mass, kilograms. FLEXITY 2: 40.9 t = 40,900 kg.</summary>
    public double EmptyMassKg { get; set; } = 40_900.0;

    /// <summary>Maximum speed, m/s. FLEXITY 2: 70 km/h = 19.444 m/s.</summary>
    public double MaxSpeedMetersPerSecond { get; set; } = 70_000.0 / 3600.0;

    /// <summary>Maximum tractive power, watts. FLEXITY 2: one 120 kW motor.
    /// NOTE: the datasheet lists FOUR 120 kW motors; if the plant should use total
    /// installed traction this becomes 480 kW. Kept at single-motor to match the
    /// team's current wireframe figure — confirm which the physics intends.</summary>
    public double MaxPowerWatts { get; set; } = 120_000.0;

    /// <summary>Medium (service) acceleration limit, m/s^2. FLEXITY 2: 0.5 m/s^2.</summary>
    public double MaxAccelMetersPerSecondSquared { get; set; } = 0.5;

    /// <summary>Service brake deceleration, m/s^2 (magnitude). FLEXITY 2: 1.2 m/s^2.</summary>
    public double ServiceDecelMetersPerSecondSquared { get; set; } = 1.2;

    /// <summary>Emergency brake deceleration, m/s^2 (magnitude). FLEXITY 2: 2.73 m/s^2.</summary>
    public double EmergencyDecelMetersPerSecondSquared { get; set; } = 2.73;

    /// <summary>Seated passenger capacity. FLEXITY 2: 74.</summary>
    public int SeatedCapacity { get; set; } = 74;

    /// <summary>Standing passenger capacity. FLEXITY 2: 148 (at 4 pass/m^2).</summary>
    public int StandingCapacity { get; set; } = 148;

    /// <summary>Crew per car (per lecture: 1 per car).</summary>
    public int CrewPerCar { get; set; } = 1;

    public int PassengerCapacity => SeatedCapacity + StandingCapacity; // 222
    public int CrewCount => Consist * CrewPerCar;

    /// <summary>Average passenger mass, kg (per lecture: 170 lb = 77.11 kg).</summary>
    public const double PassengerMassKg = 77.1107;
}
