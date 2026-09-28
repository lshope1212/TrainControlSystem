namespace TrackController.Core.Models;

/// <summary>
/// Placeholder configuration for a wayside controller (which blocks it owns, etc.).
/// PLC program loading is not implemented yet.
/// </summary>
public class ControllerConfiguration
{
    public string WaysideId { get; set; } = string.Empty;

    public IList<string> OwnedBlockIds { get; } = new List<string>();
}
