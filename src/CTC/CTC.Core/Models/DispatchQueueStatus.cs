namespace CTC.Core.Models;

/// <summary>
/// CTC-internal status of an entry in the dispatch queue. There is no "released" value:
/// a train whose release succeeded leaves the queue and becomes a
/// <see cref="DispatchedTrainState"/>, which is the single record that it was dispatched.
/// </summary>
public enum DispatchQueueStatus
{
    /// <summary>Waiting for the system time to reach its departure time (or for a retry).</summary>
    Queued = 0,

    /// <summary>
    /// A MovementRequest for this train is being sent right now. Other dispatch passes skip
    /// it so it cannot be sent twice; it returns to <see cref="Queued"/> if the send fails.
    /// </summary>
    Dispatching,
}
