using TrainController.Abstractions.Fleet;
using TrainController.Abstractions.Inputs;

namespace TrainController.Abstractions.Validation;

/// <summary>
/// Structural validation of a <see cref="TrainControllerInput"/> before any controller sees it.
/// </summary>
/// <remarks>
/// Rejects values that are physically meaningless or could corrupt controller state (NaN,
/// infinity, negative speeds/distances, non-positive timestep, unknown train, negative gains).
/// Deliberately does NOT reject merely unusual values such as an actual speed above the
/// vehicle maximum: the Test UI must be able to inject such edge cases, and the controllers
/// must handle them.
/// </remarks>
public static class TrainControllerInputValidator
{
    public static ValidationResult Validate(TrainControllerInput? input)
    {
        if (input is null)
        {
            return ValidationResult.From(new[] { "Input is null." });
        }

        var errors = new List<string>();

        if (!TrainFleet.IsKnownTrain(input.TrainId))
        {
            errors.Add($"Unknown TrainId '{input.TrainId}'.");
        }

        if (input.TickId < 0)
        {
            errors.Add("TickId must be >= 0.");
        }

        NonNegative(errors, input.SimulationTimeSeconds, "SimulationTimeSeconds");

        if (!double.IsFinite(input.DeltaTimeSeconds) || input.DeltaTimeSeconds <= 0.0)
        {
            errors.Add("DeltaTimeSeconds must be finite and > 0.");
        }

        if (input.Model is null || input.Driver is null || input.Engineer is null
            || input.Vehicle is null || input.Policy is null)
        {
            errors.Add("Input sections (Model, Driver, Engineer, Vehicle, Policy) must all be present.");
            return ValidationResult.From(errors);
        }

        var model = input.Model;
        NonNegative(errors, model.ActualSpeedMetersPerSecond, "Model.ActualSpeedMetersPerSecond");
        NonNegative(errors, model.AuthorizedSpeedMetersPerSecond, "Model.AuthorizedSpeedMetersPerSecond");
        NonNegative(errors, model.RemainingAuthorityMeters, "Model.RemainingAuthorityMeters");
        Finite(errors, model.CabinTemperatureCelsius, "Model.CabinTemperatureCelsius");

        if (model.Beacon is null)
        {
            errors.Add("Model.Beacon must be present (use BeaconData.None).");
        }
        else if (model.Beacon.IsValid)
        {
            NonNegative(errors, model.Beacon.DistanceToStationMeters, "Model.Beacon.DistanceToStationMeters");
        }

        var driver = input.Driver;
        NonNegative(errors, driver.RequestedSpeedMetersPerSecond, "Driver.RequestedSpeedMetersPerSecond");
        Finite(errors, driver.CabinTemperatureSetpointCelsius, "Driver.CabinTemperatureSetpointCelsius");
        if (driver.AnnouncementRequest is null)
        {
            errors.Add("Driver.AnnouncementRequest must not be null (use empty).");
        }

        if (!EngineerSettings.IsValidGain(input.Engineer.Kp))
        {
            errors.Add("Engineer.Kp must be finite and >= 0.");
        }

        if (!EngineerSettings.IsValidGain(input.Engineer.Ki))
        {
            errors.Add("Engineer.Ki must be finite and >= 0.");
        }

        var vehicle = input.Vehicle;
        Positive(errors, vehicle.MaxSpeedMetersPerSecond, "Vehicle.MaxSpeedMetersPerSecond");
        Positive(errors, vehicle.ServiceBrakeDecelerationMetersPerSecondSquared, "Vehicle.ServiceBrakeDecelerationMetersPerSecondSquared");
        Positive(errors, vehicle.EmergencyBrakeDecelerationMetersPerSecondSquared, "Vehicle.EmergencyBrakeDecelerationMetersPerSecondSquared");
        Positive(errors, vehicle.TotalRatedPowerWatts, "Vehicle.TotalRatedPowerWatts");

        var policy = input.Policy;
        NonNegative(errors, policy.StoppedSpeedThresholdMetersPerSecond, "Policy.StoppedSpeedThresholdMetersPerSecond");
        NonNegative(errors, policy.StationDistanceThresholdMeters, "Policy.StationDistanceThresholdMeters");
        NonNegative(errors, policy.StationBrakingMarginMeters, "Policy.StationBrakingMarginMeters");
        NonNegative(errors, policy.AuthorityBrakingMarginMeters, "Policy.AuthorityBrakingMarginMeters");
        NonNegative(errors, policy.StationDwellTimeSeconds, "Policy.StationDwellTimeSeconds");

        if (!Enum.IsDefined(policy.TrackSignalLossResponse))
        {
            errors.Add("Policy.TrackSignalLossResponse is not a defined value.");
        }

        if (!Enum.IsDefined(driver.Mode))
        {
            errors.Add("Driver.Mode is not a defined value.");
        }

        return ValidationResult.From(errors);
    }

    private static void Finite(List<string> errors, double value, string name)
    {
        if (!double.IsFinite(value))
        {
            errors.Add($"{name} must be finite.");
        }
    }

    private static void NonNegative(List<string> errors, double value, string name)
    {
        if (!double.IsFinite(value) || value < 0.0)
        {
            errors.Add($"{name} must be finite and >= 0.");
        }
    }

    private static void Positive(List<string> errors, double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0.0)
        {
            errors.Add($"{name} must be finite and > 0.");
        }
    }
}
