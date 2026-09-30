namespace CTC.Core.Models;

/// <summary>
/// A scheduled train waiting to be released for movement.
/// </summary>
public class DispatchQueueEntry
{
    public string TrainId { get; set; } = string.Empty;

    /// <summary>Departure time as time of simulation day.</summary>
    public TimeSpan DepartureTime { get; set; }

    public string FirstStation { get; set; } = string.Empty;

    public DispatchQueueStatus QueueStatus { get; set; } = DispatchQueueStatus.Queued;
}
