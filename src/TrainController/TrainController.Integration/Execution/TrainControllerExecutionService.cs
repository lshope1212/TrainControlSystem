using System.Diagnostics;
using TrainController.Abstractions.Fleet;
using TrainController.Abstractions.Inputs;
using TrainController.Abstractions.Outputs;
using TrainController.Abstractions.Validation;
using TrainController.Integration.Backends;
using TrainController.Integration.Logging;
using TrainController.Integration.Presentation;
using TrainController.Integration.Routing;

namespace TrainController.Integration.Execution;

/// <summary>
/// Thin execution boundary around the backends: shared input validation, fixed routing,
/// timing, output validation, conversion of every failure into a fail-safe output, and
/// logging of integration / safety events. Contains NO control-law calculations.
/// </summary>
/// <remarks>
/// Failure handling never falls back to another backend: a Hardware train whose controller
/// is unavailable gets a fail-safe output (power 0, emergency brake), not a Software output.
/// </remarks>
public sealed class TrainControllerExecutionService
{
    private const string Category = "Execution";

    private readonly TrainControllerRouter _router;
    private readonly ITrainControllerEventLog _log;
    private readonly HashSet<string> _routedTrains = new HashSet<string>(StringComparer.Ordinal);

    public TrainControllerExecutionService(TrainControllerRouter router, ITrainControllerEventLog? log = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _log = log ?? NullTrainControllerEventLog.Instance;
    }

    /// <summary>
    /// Executes one controller step for one train. Always returns a validated output for
    /// exactly <paramref name="input"/>.TrainId / TickId; only cancellation propagates.
    /// </summary>
    /// <param name="previous">The train's previous output (fail-safe keeps lights / cabin / station info from it).</param>
    public async Task<TrainControllerOutput> ExecuteAsync(
        TrainControllerInput input,
        TrainControllerOutput? previous,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var inputValidation = TrainControllerInputValidator.Validate(input);
        if (!inputValidation.IsValid)
        {
            var reason = $"Invalid controller input: {inputValidation}";
            _log.Log(TrainControllerLogLevel.Warning, Category, input.TrainId, reason);
            return FailSafe(input, EmergencyBrakeCause.InvalidInput, reason, previous);
        }

        var backend = _router.Resolve(input.TrainId);
        LogRoutingOnce(input.TrainId, backend.ControllerType);

        var stopwatch = Stopwatch.StartNew();
        TrainControllerOutput output;

        try
        {
            output = await backend.EvaluateAsync(input, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ControllerCommunicationException ex)
        {
            var reason = $"{backend.ControllerType} controller communication failure: {ex.Message}";
            _log.Log(TrainControllerLogLevel.Error, Category, input.TrainId, reason);
            return FailSafe(input, EmergencyBrakeCause.HardwareCommunication, reason, previous);
        }
        catch (Exception ex)
        {
            var reason = $"{backend.ControllerType} controller fault: {ex.Message}";
            _log.Log(TrainControllerLogLevel.Error, Category, input.TrainId, reason);
            return FailSafe(input, EmergencyBrakeCause.ControllerFault, reason, previous);
        }

        stopwatch.Stop();
        if (_log.IsEnabled(TrainControllerLogLevel.Diagnostic))
        {
            _log.Log(TrainControllerLogLevel.Diagnostic, Category, input.TrainId,
                $"Tick {input.TickId} evaluated by {backend.ControllerType} in {stopwatch.Elapsed.TotalMilliseconds:F2} ms.");
        }

        var outputValidation = TrainControllerOutputValidator.Validate(output, input.TrainId, input.TickId, input.Vehicle);
        if (!outputValidation.IsValid)
        {
            var cause = backend.ControllerType == ControllerType.Hardware
                ? EmergencyBrakeCause.HardwareCommunication
                : EmergencyBrakeCause.ControllerFault;
            var reason = $"Invalid {backend.ControllerType} controller output: {outputValidation}";
            _log.Log(TrainControllerLogLevel.Error, Category, input.TrainId, reason);
            return FailSafe(input, cause, reason, previous);
        }

        LogSafetyTransitions(input, previous, output);
        return output;
    }

    private static TrainControllerOutput FailSafe(
        TrainControllerInput input,
        EmergencyBrakeCause cause,
        string reason,
        TrainControllerOutput? previous) =>
        FailSafeOutput.Create(input.TrainId, input.TickId, cause, reason, input.Model, input.Policy, previous);

    private void LogRoutingOnce(string trainId, ControllerType type)
    {
        bool first;
        lock (_routedTrains)
        {
            first = _routedTrains.Add(trainId);
        }

        if (first)
        {
            _log.Log(TrainControllerLogLevel.Info, "Routing", trainId, $"Train routed to {type} controller.");
        }
    }

    private void LogSafetyTransitions(TrainControllerInput input, TrainControllerOutput? previous, TrainControllerOutput output)
    {
        var trainId = input.TrainId;
        var before = previous?.Display ?? new DriverDisplayState();
        var now = output.Display;

        var newCauses = now.EmergencyBrakeCauses & ~before.EmergencyBrakeCauses;
        if (newCauses.HasFlag(EmergencyBrakeCause.Passenger))
        {
            _log.Log(TrainControllerLogLevel.Warning, "Safety", trainId, "Passenger emergency brake request: emergency brake applied.");
        }

        if (newCauses != EmergencyBrakeCause.None && newCauses != EmergencyBrakeCause.Passenger)
        {
            _log.Log(TrainControllerLogLevel.Warning, "Safety", trainId, $"Emergency brake activated ({newCauses}).");
        }

        if (before.EmergencyBrakeLatched && !now.EmergencyBrakeLatched)
        {
            _log.Log(TrainControllerLogLevel.Info, "Safety", trainId, "Emergency brake reset by driver.");
        }

        if (now.EmergencyBrakeResetRejected)
        {
            _log.Log(TrainControllerLogLevel.Warning, "Safety", trainId, "Emergency brake reset rejected: an emergency condition is still active.");
        }

        if (now.AuthorityProtectionActive && !before.AuthorityProtectionActive)
        {
            _log.Log(TrainControllerLogLevel.Warning, "Safety", trainId, "Authority protection activated.");
        }

        if (now.StationBrakingActive && !before.StationBrakingActive)
        {
            _log.Log(TrainControllerLogLevel.Info, "Station", trainId, $"Station braking activated for {now.NextStationName}.");
        }

        if (now.DoorInterlockActive && !before.DoorInterlockActive)
        {
            _log.Log(TrainControllerLogLevel.Info, "Safety", trainId, "Door interlock activated.");
        }

        if (now.BeaconRecalibrated)
        {
            _log.Log(TrainControllerLogLevel.Info, "Station", trainId,
                $"New beacon: distance to {now.NextStationName} recalibrated to {DisplayUnits.Distance(now.DistanceToNextStationMeters)}.");
        }

        if (now.AuthorityRecalibrated)
        {
            _log.Log(TrainControllerLogLevel.Info, "Authority", trainId,
                $"Authority update: remaining authority recalibrated to {DisplayUnits.Distance(now.RemainingAuthorityMeters)}.");
        }

        switch (now.StationEvent)
        {
            case StationEvent.Arrived:
                _log.Log(TrainControllerLogLevel.Info, "Station", trainId, $"Arrived at {now.NextStationName}.");
                break;
            case StationEvent.Departed:
                _log.Log(TrainControllerLogLevel.Info, "Station", trainId, $"Departed {before.NextStationName}; waiting for next beacon.");
                break;
            case StationEvent.PassedWithoutStopping:
                _log.Log(TrainControllerLogLevel.Warning, "Station", trainId, $"Passed {before.NextStationName} without stopping.");
                break;
        }

        if (!string.IsNullOrEmpty(output.Commands.StationAnnouncement))
        {
            var source = input.Driver.AnnouncementRequest.Trim().Length > 0 ? "Driver" : "Automatic";
            _log.Log(TrainControllerLogLevel.Info, "Announcement", trainId, $"{source} announcement: \"{output.Commands.StationAnnouncement}\"");
        }

        if (now.TrackSignalLost && !before.TrackSignalLost)
        {
            _log.Log(TrainControllerLogLevel.Warning, "Safety", trainId, "Track signal lost.");
        }
    }
}
