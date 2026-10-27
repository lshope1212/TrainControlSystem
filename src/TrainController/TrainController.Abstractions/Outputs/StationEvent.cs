namespace TrainController.Abstractions.Outputs;

/// <summary>Station-tracking event that happened on this tick (display / logging).</summary>
public enum StationEvent
{
    None = 0,

    /// <summary>Stopped within the station distance threshold.</summary>
    Arrived,

    /// <summary>Left the station after serving it; the station target is cleared until the next beacon.</summary>
    Departed,

    /// <summary>Went past the station by more than the threshold without stopping; target cleared.</summary>
    PassedWithoutStopping
}
