using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;

namespace TrackController.Contracts.Messages;

/// <summary>Carries external Boolean source for validation and loading into a paused controller.</summary>
public sealed class WaysidePlcProgramMessage
{
    /// <summary>Target territory; null retains the legacy all-territory operation.</summary>
    public string? TerritoryId { get; set; }

    public string Name { get; set; } = "";

    public string Source { get; set; } = "";
}
