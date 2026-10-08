using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;

namespace TrackController.Contracts.Messages;
/// <summary>Defines permitted speed and a fixed route limit from the target block's entry, without assuming train position.</summary>
public sealed record WaysideAuthorization(
    string RequestId,
    string TrainId,
    string TargetBlockId,
    string? EntryBlockId,
    IReadOnlyList<string> PathBlockIds,
    double SuggestedSpeedMetersPerSecond,
    double SuggestedAuthorityMeters,
    double AuthorizedSpeedMetersPerSecond,
    double AuthorizedAuthorityMeters,
    string Status,
    string Reason)
{
    public string AuthorityReference { get; init; } = WaysideMovementRequestMessage.BlockEntryReference;
}
