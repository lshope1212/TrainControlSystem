using TrainController.Abstractions.Configuration;
using TrainController.Abstractions.Outputs;

namespace TrainController.Abstractions.Validation;

/// <summary>
/// Checks a controller output before it is allowed to drive the Train Model.
/// </summary>
/// <remarks>
/// Identity echo (TrainId/TickId matching the request) is checked here too, so that the
/// Software path, the Hardware path and the Pi itself all apply the same rules.
/// The brake/power consistency rule is a last-line defense: a controller that commands
/// traction while also commanding a brake is treated as faulty.
/// </remarks>
public static class TrainControllerOutputValidator
{
    /// <summary>Tolerance for comparing power against the rated limit (W).</summary>
    public const double PowerToleranceWatts = 1e-6;

    public static ValidationResult Validate(
        TrainControllerOutput? output,
        string expectedTrainId,
        long expectedTickId,
        VehicleSpecification vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        if (output is null)
        {
            return ValidationResult.From(new[] { "Output is null." });
        }

        var errors = new List<string>();

        if (!string.Equals(output.TrainId, expectedTrainId, StringComparison.Ordinal))
        {
            errors.Add($"Output TrainId '{output.TrainId}' does not match expected '{expectedTrainId}'.");
        }

        if (output.TickId != expectedTickId)
        {
            errors.Add($"Output TickId {output.TickId} does not match expected {expectedTickId}.");
        }

        if (output.Commands is null || output.Display is null)
        {
            errors.Add("Output Commands and Display must both be present.");
            return ValidationResult.From(errors);
        }

        var commands = output.Commands;
        var power = commands.PowerCommandWatts;

        if (!double.IsFinite(power))
        {
            errors.Add("PowerCommandWatts must be finite.");
        }
        else
        {
            if (power < 0.0)
            {
                errors.Add("PowerCommandWatts must be >= 0.");
            }

            if (power > vehicle.TotalRatedPowerWatts + PowerToleranceWatts)
            {
                errors.Add($"PowerCommandWatts {power} exceeds rated power {vehicle.TotalRatedPowerWatts}.");
            }

            if (power > PowerToleranceWatts && (commands.ServiceBrakeCommand || commands.EmergencyBrakeCommand))
            {
                errors.Add("PowerCommandWatts must be 0 while a brake is commanded.");
            }
        }

        if (!double.IsFinite(commands.CabinTemperatureSetpointCelsius))
        {
            errors.Add("CabinTemperatureSetpointCelsius must be finite.");
        }

        if (commands.StationAnnouncement is null)
        {
            errors.Add("StationAnnouncement must not be null.");
        }

        var display = output.Display;
        CheckFinite(errors, display.EffectiveTargetSpeedMetersPerSecond, "Display.EffectiveTargetSpeedMetersPerSecond");
        CheckFinite(errors, display.RemainingAuthorityMeters, "Display.RemainingAuthorityMeters");
        CheckFinite(errors, display.ServiceBrakeStoppingDistanceMeters, "Display.ServiceBrakeStoppingDistanceMeters");

        if (display.DistanceToNextStationMeters is double distance)
        {
            CheckFinite(errors, distance, "Display.DistanceToNextStationMeters");
        }

        if (display.DistanceToStationBrakePointMeters is double brakePoint)
        {
            CheckFinite(errors, brakePoint, "Display.DistanceToStationBrakePointMeters");
        }

        if (display.DistanceToAuthorityBrakePointMeters is double authorityBrakePoint)
        {
            CheckFinite(errors, authorityBrakePoint, "Display.DistanceToAuthorityBrakePointMeters");
        }

        if (!Enum.IsDefined(display.TractionState) || !Enum.IsDefined(display.StationEvent) || !Enum.IsDefined(display.TargetLimitedBy))
        {
            errors.Add("Display enum values must be defined.");
        }

        if (display.NextStationName is null || display.FaultReason is null || display.Alerts is null
            || display.EmergencyBrakeResetBlockedReason is null)
        {
            errors.Add("Display text fields must not be null.");
        }

        return ValidationResult.From(errors);
    }

    private static void CheckFinite(List<string> errors, double value, string name)
    {
        if (!double.IsFinite(value))
        {
            errors.Add($"{name} must be finite.");
        }
    }
}
