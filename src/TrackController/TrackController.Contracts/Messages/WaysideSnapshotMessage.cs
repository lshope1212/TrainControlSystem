using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;

namespace TrackController.Contracts.Messages;

/// <summary>Snapshot to CTC and Track Model. Commands never stand in for measured feedback.</summary>
public sealed record WaysideSnapshotMessage(
    string WaysideId,
    string LineName,
    DateTimeOffset EvaluatedAtUtc,
    DateTimeOffset ValidUntilUtc,
    double SimulationSeconds,
    [property: JsonPropertyName("paused")] bool IsPaused,
    double SpeedMultiplier,
    [property: JsonPropertyName("hardwareMode")] bool IsHardwareMode,
    [property: JsonPropertyName("maintenanceMode")] bool IsMaintenanceMode,
    string PlcName,
    string PlcStatus,
    IReadOnlyList<WaysideBlockStatus> Blocks,
    IReadOnlyList<WaysideAuthorization> Authorizations)
{
    /// <summary>Feedback from adjacent foreign blocks. Observations confer no command or movement authority.</summary>
    public IReadOnlyList<WaysideNeighborObservation> NeighborObservations { get; init; } = [];
}
