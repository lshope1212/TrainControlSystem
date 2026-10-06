using CTC.Core.Models;

namespace CTC.Wpf.ViewModels;

/// <summary>
/// Read-only presentation of a <see cref="DispatchQueueEntry"/> for the Dispatch Queue grid.
/// </summary>
public class DispatchQueueEntryViewModel
{
    public DispatchQueueEntryViewModel(DispatchQueueEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        TrainId = entry.TrainId;
        DepartureTime = entry.DepartureTime.ToString(@"hh\:mm\:ss");
    }

    public string TrainId { get; }

    /// <summary>Formatted HH:mm:ss.</summary>
    public string DepartureTime { get; }
}
