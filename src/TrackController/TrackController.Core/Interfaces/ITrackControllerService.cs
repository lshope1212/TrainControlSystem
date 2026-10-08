using TrackController.Contracts.Messages;
using TrackController.Core.Models;
using TrainControl.Contracts.Messages;

namespace TrackController.Core.Interfaces;

/// <summary>
/// Application boundary for the wayside controller subsystem.
/// The cross-subsystem contract lives in TrainControl.Contracts.
/// </summary>
public interface ITrackControllerService
{
    ControllerState State { get; }

    ControllerConfiguration Configuration { get; }

    /// <summary>Reevaluates current inputs and returns outputs with a bounded validity interval.</summary>
    WaysideSnapshotMessage GetSnapshot();

    /// <summary>Returns a defensive copy so callers cannot alter the configured topology.</summary>
    WaysideConfigurationMessage GetConfiguration();

    /// <summary>Replaces a validated layout only when the existing territory can be safely reset.</summary>
    void Configure(WaysideConfigurationMessage configuration);

    /// <summary>Pauses operation and loads validated Boolean source; an invalid replacement disables the previous program.</summary>
    void LoadPlc(string name, string source);

    /// <summary>Selects the local evaluation strategy and pauses before the new strategy can authorize movement.</summary>
    void SetHardwareMode(bool hardware);

    /// <summary>Controls maintenance access and withholds automatic movement while it is enabled.</summary>
    void SetMaintenanceMode(bool enabled);

    /// <summary>Applies pause and simulation speed settings while preserving wall-clock feedback timeouts.</summary>
    void SetSimulation(WaysideSimulationMessage message);

    /// <summary>Validates the full measurement batch before applying any block updates.</summary>
    void ApplyFeedback(WaysideFeedbackMessage message);

    /// <summary>Validates the request origin and route before evaluating conservative movement limits.</summary>
    void SubmitMovement(WaysideMovementRequestMessage message);

    /// <summary>Accepts a switch request only when its complete protection zone permits movement.</summary>
    string RequestSwitch(SwitchPositionRequestMessage message);

    /// <summary>Closes a clear block immediately or holds an occupied block pending safe clearance.</summary>
    string RequestMaintenance(MaintenanceRequestMessage message);

    /// <summary>Applies maintenance commands only when the corresponding equipment interlocks permit them.</summary>
    void ApplyManual(WaysideManualCommandMessage message);
}
