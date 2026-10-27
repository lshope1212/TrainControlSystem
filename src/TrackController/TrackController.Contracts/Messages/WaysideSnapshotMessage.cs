using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;

namespace TrackController.Contracts.Messages;

/// <summary>Runtime and diagnostic snapshot, converted into each external module's wire messages. Commands never stand in for measured feedback.</summary>
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
    IReadOnlyList<WaysideBlockStatus> Blocks)
{
    /// <summary>Movement results are stored on their target blocks and serialized there once.</summary>
    [JsonIgnore]
    public IReadOnlyList<WaysideBlockStatus> Authorizations => Blocks.Where(block => block.Request is not null).ToArray();

    /// <summary>Feedback from adjacent foreign blocks. Observations confer no command or movement authority.</summary>
    public IReadOnlyList<WaysideBlockStatus> NeighborObservations { get; init; } = [];

    /// <summary>Creates a copy with unavailable observations and stopped outputs after communication loss.</summary>
    public WaysideSnapshotMessage CreateStopSnapshot(string reason)
    {
        var now = DateTimeOffset.UtcNow;
        return this with
        {
            EvaluatedAtUtc = now,
            ValidUntilUtc = now.AddSeconds(1),
            IsPaused = true,
            PlcStatus = reason,
            Blocks = Blocks.Select(block => block with { IsFresh = false, Occupancy = OccupancyState.Unknown, IsReservedByCtc = null, CommandedSignal = SignalState.Red, CommandedCrossing = CrossingState.Closed, CommandedSwitch = SwitchPosition.Unknown, ReportedSignal = SignalState.Unknown, ReportedCrossing = CrossingState.Unknown, ReportedSwitch = SwitchPosition.Unknown, ActualSpeedMetersPerSecond = null, Status = reason, AuthorizedSpeedMetersPerSecond = 0, AuthorizedAuthorityMeters = 0, MovementStatus = block.Request is null ? "" : "Withheld", MovementReason = block.Request is null ? "" : reason }).ToArray(),
            NeighborObservations = NeighborObservations.Select(neighbor => neighbor with { IsFresh = false, Occupancy = OccupancyState.Unknown, ReportedSignal = SignalState.Unknown, ReportedSwitch = SwitchPosition.Unknown, ReportedCrossing = CrossingState.Unknown, IsReservedByCtc = null, ActualSpeedMetersPerSecond = null }).ToArray()
        };
    }
}
