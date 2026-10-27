using TrackController.Contracts.Messages;

namespace TrackController.TestUI.Wpf.ViewModels;

/// <summary>Presents one authorization with display units and stale-output handling.</summary>
public sealed record MovementRow(WaysideBlockStatus Value, bool IsCurrent = true)
{
    public string Train => string.IsNullOrWhiteSpace(Value.Request!.TrainId) ? "—" : Value.Request!.TrainId;

    public string Target => Value.Request!.TargetBlockId;

    public string Path => string.Join(" → ", Value.Request!.PathBlockIds);

    public string SuggestedSpeed => DisplayUnits.FormatMph(Value.Request!.SuggestedSpeedMetersPerSecond);

    public string RequestedAuthority => DisplayUnits.FormatFeet(Value.Request!.SuggestedAuthorityMeters);

    public string AuthorizedSpeed => IsCurrent ? DisplayUnits.FormatMph(Value.AuthorizedSpeedMetersPerSecond) : "—";

    public string AuthorizedAuthority => IsCurrent ? DisplayUnits.FormatFeet(Value.AuthorizedAuthorityMeters) : "—";

    public string Status => IsCurrent ? Value.MovementStatus : "Unavailable";

    public string Reason => IsCurrent ? Value.MovementReason : "Controller output unavailable.";
}
