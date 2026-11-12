using TrackController.Contracts.Messages;

namespace TrackController.Wpf.ViewModels;

/// <summary>Presents one authorization using the operator window's display units.</summary>
public sealed record MovementRow(WaysideBlockStatus Value)
{
    public string Train => string.IsNullOrWhiteSpace(Value.Request!.TrainId) ? "—" : Value.Request!.TrainId;

    public string Target => Value.Request!.TargetBlockId;

    public string Path => string.Join(" → ", Value.Request!.PathBlockIds);

    public string SuggestedSpeed => DisplayUnits.FormatMph(Value.Request!.SuggestedSpeedMetersPerSecond);

    public string RequestedAuthority => DisplayUnits.FormatFeet(Value.Request!.SuggestedAuthorityMeters);

    public string AuthorizedSpeed => DisplayUnits.FormatMph(Value.AuthorizedSpeedMetersPerSecond);

    public string AuthorizedAuthority => DisplayUnits.FormatFeet(Value.AuthorizedAuthorityMeters);

    public string Status => Value.MovementStatus;

    public string Reason => Value.MovementReason;

    /// <summary>Whether <paramref name="other"/> displays exactly the same values (the underlying statuses may be different objects).</summary>
    public bool ShowsSameAs(MovementRow other) => Train == other.Train && Target == other.Target && Path == other.Path
        && SuggestedSpeed == other.SuggestedSpeed && RequestedAuthority == other.RequestedAuthority && AuthorizedSpeed == other.AuthorizedSpeed
        && AuthorizedAuthority == other.AuthorizedAuthority && Status == other.Status && Reason == other.Reason;
}
