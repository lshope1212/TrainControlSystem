namespace TrackController.Wpf.ViewModels;

/// <summary>Names a bundled sample deployment so the operator can start any supported line.</summary>
public sealed record SampleLine(string Name, string DeploymentFile)
{
    public override string ToString() => Name;
}
