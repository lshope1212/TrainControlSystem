using System.Globalization;
using TrackController.Contracts.Messages;
using TrainControl.Contracts.Enums;

namespace TrackController.Wpf.ViewModels;

/// <summary>Presents one authorization with display units and stale-output handling.</summary>
public sealed record MovementRow(WaysideAuthorization Value, bool IsCurrent = true)
{
    public string Train => string.IsNullOrWhiteSpace(Value.TrainId) ? "—" : Value.TrainId;

    public string Target => Value.TargetBlockId;

    public string Path => string.Join(" → ", Value.PathBlockIds);

    public string SuggestedSpeed => DisplayUnits.FormatMph(Value.SuggestedSpeedMetersPerSecond);

    public string RequestedAuthority => DisplayUnits.FormatFeet(Value.SuggestedAuthorityMeters);

    public string AuthorizedSpeed => IsCurrent ? DisplayUnits.FormatMph(Value.AuthorizedSpeedMetersPerSecond) : "—";

    public string AuthorizedAuthority => IsCurrent ? DisplayUnits.FormatFeet(Value.AuthorizedAuthorityMeters) : "—";

    public string Status => IsCurrent ? Value.Status : "Unavailable";

    public string Reason => IsCurrent ? Value.Reason : "Controller output unavailable.";
}
