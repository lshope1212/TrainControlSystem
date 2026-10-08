using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;

namespace TrackController.Contracts.Messages;
/// <summary>Anchors permitted speed and distance to a specific request origin and route.</summary>
public sealed record WaysideAuthorization(
    string RequestId,
    string TrainId,
    string TargetBlockId,
    string? EntryBlockId,
    double FrontOffsetMeters,
    IReadOnlyList<string> PathBlockIds,
    double SuggestedSpeedMetersPerSecond,
    double SuggestedAuthorityMeters,
    double AuthorizedSpeedMetersPerSecond,
    double AuthorizedAuthorityMeters,
    string Status,
    string Reason);
