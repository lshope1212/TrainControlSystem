using TrainController.Abstractions.Configuration;
using TrainController.Core.Control;

namespace TrainController.Core.Safety;

/// <summary>Braking demanded by authority protection for one tick.</summary>
public readonly record struct AuthorityDemand(bool ServiceBrake, bool EmergencyBrake)
{
    public bool Active => ServiceBrake || EmergencyBrake;
}

/// <summary>
/// Safety-critical: never run past the end of the remaining authority.
/// </summary>
/// <remarks>
/// <para>
/// Service brake when the remaining authority is no more than the service-brake stopping
/// distance plus the configured authority margin plus one control period of travel
/// (v·dt — the train cannot react before the next tick; this compensates discretization and
/// is not a safety margin).
/// </para>
/// <para>
/// Emergency brake when the train is moving and the authority is shorter than even the
/// emergency-brake stopping distance (e.g. authority suddenly reduced).
/// </para>
/// <para>A stopped train with exhausted authority is held with the service brake.</para>
/// </remarks>
public static class AuthorityProtection
{
    public static AuthorityDemand Evaluate(
        double actualSpeedMetersPerSecond,
        double remainingAuthorityMeters,
        double deltaTimeSeconds,
        VehicleSpecification vehicle,
        ControllerPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(policy);

        var v = actualSpeedMetersPerSecond;
        var stopped = v <= policy.StoppedSpeedThresholdMetersPerSecond;

        if (stopped)
        {
            return new AuthorityDemand(remainingAuthorityMeters <= policy.AuthorityBrakingMarginMeters, false);
        }

        var serviceStop = StoppingDistance.Compute(v, vehicle.ServiceBrakeDecelerationMetersPerSecondSquared)
            + policy.AuthorityBrakingMarginMeters
            + v * deltaTimeSeconds;
        var emergencyStop = StoppingDistance.Compute(v, vehicle.EmergencyBrakeDecelerationMetersPerSecondSquared);

        var emergency = remainingAuthorityMeters < emergencyStop;
        var service = remainingAuthorityMeters <= serviceStop;

        return new AuthorityDemand(service, emergency);
    }

    /// <summary>
    /// Distance to the point where <see cref="Evaluate"/> starts demanding the service brake
    /// (same thresholds). ≤ 0 means authority braking is active now.
    /// </summary>
    public static double DistanceToBrakePoint(
        double actualSpeedMetersPerSecond,
        double remainingAuthorityMeters,
        double deltaTimeSeconds,
        VehicleSpecification vehicle,
        ControllerPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(policy);

        var v = actualSpeedMetersPerSecond;
        if (v <= policy.StoppedSpeedThresholdMetersPerSecond)
        {
            return remainingAuthorityMeters - policy.AuthorityBrakingMarginMeters;
        }

        return remainingAuthorityMeters
            - StoppingDistance.Compute(v, vehicle.ServiceBrakeDecelerationMetersPerSecondSquared)
            - policy.AuthorityBrakingMarginMeters
            - v * deltaTimeSeconds;
    }
}
