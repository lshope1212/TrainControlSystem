using TrackController.Contracts.Messages;
using TrainControl.Contracts.Enums;

namespace TrackController.Runtime;

/// <summary>Creates conservative observations and zero movement authority after a runtime failure.</summary>
public static class UnavailableOutput
{
    /// <summary>Replaces movement and equipment outputs with conservative values after communication loss.</summary>
    public static WaysideSnapshotMessage CreateStopSnapshot(WaysideSnapshotMessage previous, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        return previous with
        {
            EvaluatedAtUtc = now,
            ValidUntilUtc = now.AddSeconds(1),
            IsPaused = true,
            PlcStatus = reason,
            Blocks = previous.Blocks.Select(blockStatus => blockStatus with { IsFresh = false, Occupancy = OccupancyState.Unknown, IsReservedByCtc = null, CommandedSignal = SignalState.Red, CommandedCrossing = CrossingState.Closed, CommandedSwitch = SwitchPosition.Unknown, ReportedSignal = SignalState.Unknown, ReportedCrossing = CrossingState.Unknown, ReportedSwitch = SwitchPosition.Unknown, ActualSpeedMetersPerSecond = null, Status = reason }).ToArray(),
            Authorizations = previous.Authorizations.Select(authorization => authorization with { AuthorizedSpeedMetersPerSecond = 0, AuthorizedAuthorityMeters = 0, Status = "Withheld", Reason = reason }).ToArray(),
            NeighborObservations = previous.NeighborObservations.Select(neighbor => neighbor with { IsFresh = false, Occupancy = OccupancyState.Unknown, Signal = SignalState.Unknown, Switch = SwitchPosition.Unknown, Crossing = CrossingState.Unknown, IsReservedByCtc = null }).ToArray()
        };
    }
}
