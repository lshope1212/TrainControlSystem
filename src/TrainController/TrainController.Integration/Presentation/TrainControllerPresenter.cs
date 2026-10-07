using TrainControl.Contracts.Enums;
using TrainController.Abstractions.Configuration;
using TrainController.Abstractions.Fleet;
using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;
using TrainController.Integration.Hardware;
using TrainController.Integration.State;

namespace TrainController.Integration.Presentation;

/// <summary>Text + tone for one displayed value.</summary>
public sealed record DisplayValue(string Text, DisplayTone Tone = DisplayTone.Neutral)
{
    public static DisplayValue Missing { get; } = new DisplayValue(DisplayUnits.Missing, DisplayTone.Muted);

    public override string ToString() => Text;
}

/// <summary>Data for the Main UI horizontal distance-guidance bar, in feet (display units).</summary>
public sealed record GuidanceBarModel(
    bool HasData,
    double? StationFeet,
    string StationName,
    double? AuthorityEndFeet,
    double? BrakePointFeet,
    bool BrakingDue)
{
    public static GuidanceBarModel Empty { get; } = new GuidanceBarModel(false, null, string.Empty, null, null, false);
}

/// <summary>Everything the Main UI shows for the selected train (read-only, imperial).</summary>
public sealed record MainUiTrainStatus
{
    public string TrainId { get; init; } = string.Empty;

    public ControllerType ControllerType { get; init; }

    /// <summary>"SOFTWARE CONTROLLED" / "HARDWARE CONTROLLED".</summary>
    public string ControllerSource { get; init; } = string.Empty;

    public bool IsHardware { get; init; }

    /// <summary>Pi link state for Hardware trains; "Not applicable" for Software trains.</summary>
    public DisplayValue HardwareLink { get; init; } = DisplayValue.Missing;

    /// <summary>Running / not dispatched (from model input), or fail-safe.</summary>
    public DisplayValue TrainState { get; init; } = DisplayValue.Missing;

    public DisplayValue ActualSpeed { get; init; } = DisplayValue.Missing;

    public DisplayValue DriverRequestedSpeed { get; init; } = DisplayValue.Missing;

    public DisplayValue AuthorizedSpeed { get; init; } = DisplayValue.Missing;

    public DisplayValue EffectiveTargetSpeed { get; init; } = DisplayValue.Missing;

    /// <summary>Which constraint set the effective target (computed by the controller).</summary>
    public DisplayValue TargetLimitedBy { get; init; } = DisplayValue.Missing;

    public DisplayValue RemainingAuthority { get; init; } = DisplayValue.Missing;

    public DisplayValue CommandedPower { get; init; } = DisplayValue.Missing;

    public DisplayValue ServiceBrake { get; init; } = DisplayValue.Missing;

    public DisplayValue EmergencyBrake { get; init; } = DisplayValue.Missing;

    public DisplayValue EmergencyCauses { get; init; } = DisplayValue.Missing;

    /// <summary>Whether a driver E-brake reset is possible now, and if not, why.</summary>
    public DisplayValue EmergencyReset { get; init; } = DisplayValue.Missing;

    /// <summary>Power status: why the commanded power is what it is (e.g. "0: holding brake while stopped").</summary>
    public DisplayValue PowerStatus { get; init; } = DisplayValue.Missing;

    public DisplayValue NextStation { get; init; } = DisplayValue.Missing;

    public DisplayValue DistanceToStation { get; init; } = DisplayValue.Missing;

    public DisplayValue PlatformSide { get; init; } = DisplayValue.Missing;

    public DisplayValue StationGuidance { get; init; } = DisplayValue.Missing;

    public DisplayValue BrakePoint { get; init; } = DisplayValue.Missing;

    public DisplayValue LeftDoors { get; init; } = DisplayValue.Missing;

    public DisplayValue RightDoors { get; init; } = DisplayValue.Missing;

    public DisplayValue ExteriorLights { get; init; } = DisplayValue.Missing;

    public DisplayValue CabinTemperature { get; init; } = DisplayValue.Missing;

    /// <summary>Most recent announcement sent (with tick), persisting after its one-tick command.</summary>
    public DisplayValue Announcement { get; init; } = DisplayValue.Missing;

    /// <summary>Driver announcement waiting for this train's next tick, if any.</summary>
    public string PendingAnnouncement { get; init; } = string.Empty;

    public DisplayValue Fault { get; init; } = DisplayValue.Missing;

    /// <summary>Non-empty while Kp/Ki are still the untuned startup placeholder.</summary>
    public string GainsWarning { get; init; } = string.Empty;

    public IReadOnlyList<string> Alerts { get; init; } = Array.Empty<string>();

    public GuidanceBarModel Guidance { get; init; } = GuidanceBarModel.Empty;
}

/// <summary>One row of the Main UI fleet table (all ten trains, running in the background).</summary>
public sealed record FleetRowStatus(
    string TrainId,
    string Controller,
    DisplayValue State,
    string ActualSpeed,
    string Power,
    DisplayValue Brakes,
    DisplayValue Status);

/// <summary>
/// Test UI output panel: ONLY the values the Train Controller sends to the Train Model.
/// </summary>
public sealed record TestUiOutputs
{
    public bool HasOutput { get; init; }

    public string TickText { get; init; } = DisplayUnits.Missing;

    public DisplayValue PowerCommand { get; init; } = DisplayValue.Missing;

    public DisplayValue ServiceBrakeCommand { get; init; } = DisplayValue.Missing;

    public DisplayValue EmergencyBrakeCommand { get; init; } = DisplayValue.Missing;

    public DisplayValue LeftDoorCommand { get; init; } = DisplayValue.Missing;

    public DisplayValue RightDoorCommand { get; init; } = DisplayValue.Missing;

    public DisplayValue ExteriorLightCommand { get; init; } = DisplayValue.Missing;

    public DisplayValue CabinTemperatureSetpoint { get; init; } = DisplayValue.Missing;

    /// <summary>Announcement in the latest command (non-empty only on the tick it is sent).</summary>
    public DisplayValue StationAnnouncement { get; init; } = DisplayValue.Missing;

    /// <summary>Most recent announcement sent to the Train Model, with its tick (persists).</summary>
    public DisplayValue LastAnnouncementSent { get; init; } = DisplayValue.Missing;
}

/// <summary>
/// One row of the Test UI fleet table: Train Model boundary I/O only (no controller type,
/// no Driver / Engineer data).
/// </summary>
public sealed record TestFleetRowStatus(
    string TrainId,
    DisplayValue Active,
    string ActualSpeedInput,
    string PowerCommand,
    DisplayValue ServiceBrakeCommand,
    DisplayValue EmergencyBrakeCommand);

/// <summary>
/// Builds framework-neutral display snapshots from Train Controller state. Formatting and
/// unit conversion only — no control decisions. Shared by the Main UI and Test UI view models.
/// </summary>
public static class TrainControllerPresenter
{
    public static string ControllerSourceText(ControllerType type) =>
        type == ControllerType.Hardware ? "HARDWARE CONTROLLED" : "SOFTWARE CONTROLLED";

    public static string ControllerShortText(ControllerType type) =>
        type == ControllerType.Hardware ? "Hardware" : "Software";

    public static DisplayValue HardwareLinkText(HardwareConnectionState? state) => state switch
    {
        HardwareConnectionState.Active => new DisplayValue("Active", DisplayTone.Good),
        HardwareConnectionState.Ready => new DisplayValue("Ready", DisplayTone.Good),
        HardwareConnectionState.Connecting => new DisplayValue("Connecting", DisplayTone.Warning),
        HardwareConnectionState.Faulted => new DisplayValue("Faulted", DisplayTone.Danger),
        HardwareConnectionState.Disconnected => new DisplayValue("Disconnected", DisplayTone.Danger),
        _ => new DisplayValue("Unknown", DisplayTone.Warning),
    };

    public static MainUiTrainStatus BuildMain(TrainRuntimeSlot slot, HardwareConnectionState? hardwareState)
    {
        ArgumentNullException.ThrowIfNull(slot);

        var output = slot.LastOutput;
        var model = slot.LastModelInput;
        var driver = slot.Driver.Peek();
        var isHardware = slot.ControllerType == ControllerType.Hardware;

        var status = new MainUiTrainStatus
        {
            TrainId = slot.TrainId,
            ControllerType = slot.ControllerType,
            ControllerSource = ControllerSourceText(slot.ControllerType),
            IsHardware = isHardware,
            HardwareLink = isHardware ? HardwareLinkText(hardwareState) : new DisplayValue("Not applicable", DisplayTone.Muted),
            DriverRequestedSpeed = driver.Mode == OperatingMode.Manual
                ? new DisplayValue(DisplayUnits.Speed(driver.RequestedSpeedMetersPerSecond))
                : new DisplayValue("Automatic", DisplayTone.Muted),
            PendingAnnouncement = slot.Driver.AnnouncementPending.Length > 0
                ? $"Queued for next tick: \"{slot.Driver.AnnouncementPending}\""
                : string.Empty,
            Announcement = LastAnnouncementText(slot),
            GainsWarning = slot.Engineer.IsUntunedStartupPlaceholder
                ? "Kp/Ki are the untuned startup placeholder (1.0 / 0.0) — not operating gains. Tune before running."
                : string.Empty,
        };

        if (output is null || model is null)
        {
            return status with
            {
                TrainState = new DisplayValue("No controller output yet (train not running)", DisplayTone.Muted),
            };
        }

        var display = output.Display;
        var commands = output.Commands;

        var trainState = output.IsFailSafe
            ? new DisplayValue("FAIL-SAFE", DisplayTone.Danger)
            : model.IsActive
                ? new DisplayValue("Running", DisplayTone.Good)
                : new DisplayValue("Not dispatched", DisplayTone.Muted);

        return status with
        {
            TrainState = trainState,
            ActualSpeed = new DisplayValue(DisplayUnits.Speed(model.ActualSpeedMetersPerSecond)),
            AuthorizedSpeed = model.TrackSignalValid
                ? new DisplayValue(DisplayUnits.Speed(model.AuthorizedSpeedMetersPerSecond))
                : new DisplayValue("Signal lost", DisplayTone.Danger),
            EffectiveTargetSpeed = new DisplayValue(DisplayUnits.Speed(display.EffectiveTargetSpeedMetersPerSecond)),
            TargetLimitedBy = TargetConstraintText(display.TargetLimitedBy),
            RemainingAuthority = new DisplayValue(DisplayUnits.Distance(display.RemainingAuthorityMeters),
                display.AuthorityProtectionActive ? DisplayTone.Warning : DisplayTone.Neutral),
            CommandedPower = new DisplayValue(DisplayUnits.Power(commands.PowerCommandWatts)),
            ServiceBrake = OnOff(commands.ServiceBrakeCommand, DisplayTone.Warning),
            EmergencyBrake = OnOff(commands.EmergencyBrakeCommand, DisplayTone.Danger),
            EmergencyCauses = display.EmergencyBrakeCauses == EmergencyBrakeCause.None
                ? new DisplayValue("None", DisplayTone.Muted)
                : new DisplayValue(EmergencyCausesText(display.EmergencyBrakeCauses), DisplayTone.Danger),
            EmergencyReset = !display.EmergencyBrakeLatched
                ? new DisplayValue("Not needed", DisplayTone.Muted)
                : display.EmergencyBrakeResetBlockedReason.Length > 0
                    ? new DisplayValue($"Blocked: {display.EmergencyBrakeResetBlockedReason}", DisplayTone.Danger)
                    : new DisplayValue("Available (press E-brake reset)", DisplayTone.Warning),
            PowerStatus = PowerStatusText(display.TractionState),
            NextStation = string.IsNullOrEmpty(display.NextStationName)
                ? DisplayValue.Missing
                : new DisplayValue(display.NextStationName),
            DistanceToStation = display.DistanceToNextStationMeters is double toStation
                ? new DisplayValue(DisplayUnits.Distance(Math.Max(0.0, toStation)))
                : new DisplayValue("No upcoming station (waiting for beacon)", DisplayTone.Muted),
            PlatformSide = display.DistanceToNextStationMeters is null
                ? DisplayValue.Missing
                : new DisplayValue(PlatformSideText(display.PlatformSide)),
            StationGuidance = StationGuidance(display),
            BrakePoint = display.DistanceToStationBrakePointMeters is double brakePoint
                ? new DisplayValue(brakePoint <= 0.0 ? "Now" : $"in {DisplayUnits.Distance(brakePoint)}",
                    brakePoint <= 0.0 ? DisplayTone.Warning : DisplayTone.Neutral)
                : DisplayValue.Missing,
            LeftDoors = Doors(commands.LeftDoorsOpenCommand, model.LeftDoorsOpen),
            RightDoors = Doors(commands.RightDoorsOpenCommand, model.RightDoorsOpen),
            ExteriorLights = new DisplayValue(
                $"{(commands.ExteriorLightsCommand ? "On" : "Off")} (actual {(model.ExteriorLightsOn ? "on" : "off")})"),
            CabinTemperature = new DisplayValue(
                $"{DisplayUnits.Temperature(model.CabinTemperatureCelsius)} (set {DisplayUnits.Temperature(commands.CabinTemperatureSetpointCelsius)})"),
            Announcement = LastAnnouncementText(slot),
            Fault = display.ControllerFaulted
                ? new DisplayValue(display.FaultReason, DisplayTone.Danger)
                : new DisplayValue("None", DisplayTone.Muted),
            Alerts = display.Alerts,
            Guidance = new GuidanceBarModel(
                HasData: true,
                StationFeet: display.DistanceToNextStationMeters is double s ? DisplayUnits.ToFeet(Math.Max(0.0, s)) : null,
                StationName: display.NextStationName,
                AuthorityEndFeet: DisplayUnits.ToFeet(display.RemainingAuthorityMeters),
                BrakePointFeet: display.DistanceToStationBrakePointMeters is double b ? DisplayUnits.ToFeet(Math.Max(0.0, b)) : null,
                BrakingDue: display.StationBrakingAdvised || display.StationBrakingActive),
        };
    }

    public static FleetRowStatus BuildFleetRow(TrainRuntimeSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        var output = slot.LastOutput;
        var model = slot.LastModelInput;
        var controller = ControllerShortText(slot.ControllerType);

        if (output is null || model is null)
        {
            return new FleetRowStatus(slot.TrainId, controller, new DisplayValue("Idle", DisplayTone.Muted),
                DisplayUnits.Missing, DisplayUnits.Missing, DisplayValue.Missing, DisplayValue.Missing);
        }

        var brakes = output.Commands.EmergencyBrakeCommand
            ? new DisplayValue("Emergency", DisplayTone.Danger)
            : output.Commands.ServiceBrakeCommand
                ? new DisplayValue("Service", DisplayTone.Warning)
                : new DisplayValue("Released", DisplayTone.Muted);

        var statusText = output.IsFailSafe
            ? new DisplayValue("Fail-safe", DisplayTone.Danger)
            : output.Display.Alerts.Count > 0
                ? new DisplayValue(output.Display.Alerts[0], DisplayTone.Warning)
                : new DisplayValue("OK", DisplayTone.Good);

        return new FleetRowStatus(
            slot.TrainId,
            controller,
            model.IsActive ? new DisplayValue("Running", DisplayTone.Good) : new DisplayValue("Idle", DisplayTone.Muted),
            DisplayUnits.Speed(model.ActualSpeedMetersPerSecond),
            DisplayUnits.Power(output.Commands.PowerCommandWatts),
            brakes,
            statusText);
    }

    public static TestUiOutputs BuildTestOutputs(TrainRuntimeSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        var output = slot.LastOutput;
        if (output is null)
        {
            return new TestUiOutputs { LastAnnouncementSent = LastAnnouncementText(slot) };
        }

        var c = output.Commands;
        return new TestUiOutputs
        {
            HasOutput = true,
            TickText = $"Tick {output.TickId}",
            PowerCommand = new DisplayValue(DisplayUnits.Power(c.PowerCommandWatts)),
            ServiceBrakeCommand = OnOff(c.ServiceBrakeCommand, DisplayTone.Warning),
            EmergencyBrakeCommand = OnOff(c.EmergencyBrakeCommand, DisplayTone.Danger),
            LeftDoorCommand = OpenClosed(c.LeftDoorsOpenCommand),
            RightDoorCommand = OpenClosed(c.RightDoorsOpenCommand),
            ExteriorLightCommand = OnOff(c.ExteriorLightsCommand, DisplayTone.Info),
            CabinTemperatureSetpoint = new DisplayValue(DisplayUnits.Temperature(c.CabinTemperatureSetpointCelsius)),
            StationAnnouncement = string.IsNullOrEmpty(c.StationAnnouncement)
                ? new DisplayValue("(none this tick)", DisplayTone.Muted)
                : new DisplayValue(c.StationAnnouncement, DisplayTone.Info),
            LastAnnouncementSent = LastAnnouncementText(slot),
        };
    }

    public static TestFleetRowStatus BuildTestFleetRow(TrainRuntimeSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        var test = slot.TestModel.PeekModelInput();
        var output = slot.LastOutput;

        return new TestFleetRowStatus(
            slot.TrainId,
            test.IsActive ? new DisplayValue("Active", DisplayTone.Good) : new DisplayValue("Inactive", DisplayTone.Muted),
            DisplayUnits.Speed(test.ActualSpeedMetersPerSecond),
            output is null ? DisplayUnits.Missing : DisplayUnits.Power(output.Commands.PowerCommandWatts),
            output is null ? DisplayValue.Missing : OnOff(output.Commands.ServiceBrakeCommand, DisplayTone.Warning),
            output is null ? DisplayValue.Missing : OnOff(output.Commands.EmergencyBrakeCommand, DisplayTone.Danger));
    }

    /// <summary>
    /// Warnings for unusual Test UI inputs. Edge values are allowed on purpose (they are how
    /// fault cases are injected); this only makes them visible.
    /// </summary>
    public static IReadOnlyList<string> TestInputWarnings(TrainModelInput input, VehicleSpecification vehicle)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(vehicle);

        var warnings = new List<string>();
        var maxMph = DisplayUnits.Speed(vehicle.MaxSpeedMetersPerSecond);

        if (input.ActualSpeedMetersPerSecond > vehicle.MaxSpeedMetersPerSecond)
        {
            warnings.Add($"Actual speed is above the nominal vehicle maximum ({maxMph}).");
        }

        if (input.AuthorizedSpeedMetersPerSecond > vehicle.MaxSpeedMetersPerSecond)
        {
            warnings.Add($"Authorized speed is above the nominal vehicle maximum ({maxMph}).");
        }

        if (input.ActualSpeedMetersPerSecond < 0.0 || input.AuthorizedSpeedMetersPerSecond < 0.0
            || input.RemainingAuthorityMeters < 0.0
            || (input.Beacon.IsValid && input.Beacon.DistanceToStationMeters < 0.0))
        {
            warnings.Add("Negative speed/distance: the controller will reject this input (fail-safe).");
        }

        if (!input.TrackSignalValid)
        {
            warnings.Add("Track signal invalid: the controller will brake.");
        }

        if (input.PassengerEmergencyBrakeRequested)
        {
            warnings.Add("Passenger emergency brake requested.");
        }

        return warnings;
    }

    public static DisplayValue TargetConstraintText(TargetSpeedConstraint constraint) => constraint switch
    {
        TargetSpeedConstraint.DriverRequest => new DisplayValue("Driver request"),
        TargetSpeedConstraint.AuthorizedSpeed => new DisplayValue("Authorized speed"),
        TargetSpeedConstraint.VehicleMaximum => new DisplayValue("Vehicle maximum"),
        TargetSpeedConstraint.RemainingAuthority => new DisplayValue("Remaining authority", DisplayTone.Warning),
        TargetSpeedConstraint.StationStop => new DisplayValue("Station stop", DisplayTone.Info),
        TargetSpeedConstraint.TrackSignalLoss => new DisplayValue("Track signal lost", DisplayTone.Danger),
        TargetSpeedConstraint.EmergencyBrake => new DisplayValue("Emergency brake", DisplayTone.Danger),
        TargetSpeedConstraint.DriverServiceBrake => new DisplayValue("Driver service brake", DisplayTone.Warning),
        _ => DisplayValue.Missing,
    };

    public static DisplayValue LastAnnouncementText(TrainRuntimeSlot slot) =>
        slot.LastAnnouncement.Length == 0
            ? DisplayValue.Missing
            : new DisplayValue($"\"{slot.LastAnnouncement}\"  (tick {slot.LastAnnouncementTick})", DisplayTone.Info);

    public static DisplayValue PowerStatusText(TractionState state) => state switch
    {
        TractionState.Powering => new DisplayValue("Powering", DisplayTone.Good),
        TractionState.AtOrAboveTarget => new DisplayValue("0: at or above target speed", DisplayTone.Muted),
        TractionState.HoldingStopped => new DisplayValue("0: holding brake while stopped", DisplayTone.Muted),
        TractionState.ServiceBrake => new DisplayValue("0: service brake applied", DisplayTone.Warning),
        TractionState.DoorInterlock => new DisplayValue("0: door interlock", DisplayTone.Warning),
        TractionState.EmergencyBrake => new DisplayValue("0: emergency brake", DisplayTone.Danger),
        TractionState.FailSafe => new DisplayValue("0: fail-safe", DisplayTone.Danger),
        _ => DisplayValue.Missing,
    };

    public static string EmergencyCausesText(EmergencyBrakeCause causes)
    {
        var parts = new List<string>();
        if (causes.HasFlag(EmergencyBrakeCause.Driver)) parts.Add("Driver");
        if (causes.HasFlag(EmergencyBrakeCause.Passenger)) parts.Add("Passenger request");
        if (causes.HasFlag(EmergencyBrakeCause.AuthorityViolation)) parts.Add("Cannot stop within authority");
        if (causes.HasFlag(EmergencyBrakeCause.TrackSignalLoss)) parts.Add("Track signal lost");
        if (causes.HasFlag(EmergencyBrakeCause.HardwareCommunication)) parts.Add("Hardware link failure");
        if (causes.HasFlag(EmergencyBrakeCause.InvalidInput)) parts.Add("Invalid input");
        if (causes.HasFlag(EmergencyBrakeCause.ControllerFault)) parts.Add("Controller fault");
        return parts.Count == 0 ? "None" : string.Join(", ", parts);
    }

    public static string PlatformSideText(PlatformSide side) => side switch
    {
        TrainControl.Contracts.Enums.PlatformSide.Left => "Left",
        TrainControl.Contracts.Enums.PlatformSide.Right => "Right",
        TrainControl.Contracts.Enums.PlatformSide.Both => "Both",
        _ => "None",
    };

    private static DisplayValue StationGuidance(DriverDisplayState display)
    {
        if (display.AtStation)
        {
            return new DisplayValue("At station", DisplayTone.Good);
        }

        if (display.StationBrakingActive)
        {
            return new DisplayValue("Automatic station braking", DisplayTone.Info);
        }

        if (display.StationBrakingAdvised)
        {
            return new DisplayValue("BRAKE NOW for station", DisplayTone.Warning);
        }

        if (display.DistanceToNextStationMeters is null)
        {
            return display.StationEvent switch
            {
                StationEvent.Departed => new DisplayValue("Departed station; waiting for next beacon", DisplayTone.Muted),
                StationEvent.PassedWithoutStopping => new DisplayValue("Passed station without stopping", DisplayTone.Warning),
                _ => new DisplayValue("No upcoming station", DisplayTone.Muted),
            };
        }

        return new DisplayValue("Approaching", DisplayTone.Neutral);
    }

    private static DisplayValue OnOff(bool on, DisplayTone onTone) =>
        on ? new DisplayValue("On", onTone) : new DisplayValue("Off", DisplayTone.Muted);

    private static DisplayValue OpenClosed(bool open) =>
        open ? new DisplayValue("Open", DisplayTone.Warning) : new DisplayValue("Closed", DisplayTone.Muted);

    private static DisplayValue Doors(bool commandedOpen, bool reportedOpen) =>
        new DisplayValue(
            $"{(commandedOpen ? "Open" : "Closed")} (actual {(reportedOpen ? "open" : "closed")})",
            commandedOpen || reportedOpen ? DisplayTone.Warning : DisplayTone.Muted);
}
