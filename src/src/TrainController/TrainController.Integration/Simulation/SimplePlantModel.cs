using TrainController.Abstractions.Configuration;
using TrainController.Abstractions.Outputs;

namespace TrainController.Integration.Simulation;

/// <summary>
/// TEST TOOL ONLY: a deliberately simple point-mass speed model for the Test UI's "Simple physics"
/// option. It is NOT the Train Model (which owns real physics: mass with passengers, grade,
/// friction, failures) and it is never used by either controller.
/// </summary>
/// <remarks>
/// Emergency brake → −emergency deceleration; service brake → −service deceleration; otherwise
/// traction acceleration = power / (empty mass × max(v, 1 m/s)), capped at the vehicle's medium
/// acceleration. No resistance, no grade. Speed never goes below 0. Turning the option off lets
/// the tester hold the speed manually, which is how brake / engine failures are reproduced.
/// </remarks>
public static class SimplePlantModel
{
    /// <summary>Speed floor used in P = F·v so that starting from rest gives a finite force.</summary>
    public const double MinimumTractionSpeedMetersPerSecond = 1.0;

    public static double AccelerationMetersPerSecondSquared(double speedMetersPerSecond, TrainModelCommand commands, VehicleSpecification vehicle)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(vehicle);

        if (commands.EmergencyBrakeCommand)
        {
            return -vehicle.EmergencyBrakeDecelerationMetersPerSecondSquared;
        }

        if (commands.ServiceBrakeCommand)
        {
            return -vehicle.ServiceBrakeDecelerationMetersPerSecondSquared;
        }

        var power = Math.Max(0.0, commands.PowerCommandWatts);
        if (power <= 0.0 || vehicle.EmptyMassKilograms <= 0.0)
        {
            return 0.0;
        }

        var traction = power / (vehicle.EmptyMassKilograms * Math.Max(speedMetersPerSecond, MinimumTractionSpeedMetersPerSecond));
        return Math.Min(traction, vehicle.MediumAccelerationMetersPerSecondSquared);
    }

    public static double NextSpeed(double speedMetersPerSecond, TrainModelCommand commands, VehicleSpecification vehicle, double deltaTimeSeconds)
    {
        var next = speedMetersPerSecond + AccelerationMetersPerSecondSquared(speedMetersPerSecond, commands, vehicle) * deltaTimeSeconds;
        return Math.Max(0.0, next);
    }
}
