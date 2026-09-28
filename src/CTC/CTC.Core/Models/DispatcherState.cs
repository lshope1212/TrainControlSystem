namespace CTC.Core.Models;

/// <summary>
/// Placeholder state of the dispatcher's console (mode, selected train, ...).
/// </summary>
public class DispatcherState
{
    public bool IsAutomaticMode { get; set; }

    public string SelectedTrainId { get; set; } = string.Empty;
}
