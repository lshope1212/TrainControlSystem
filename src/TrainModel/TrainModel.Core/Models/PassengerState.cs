namespace TrainModel.Core.Models;

/// <summary>
/// Onboard passenger load for a train. The Train Model owns who ALIGHTS;
/// the Track Model owns who BOARDS (platform demand). Capacity is never exceeded
/// and the count never goes negative.
/// </summary>
public class PassengerState
{
    public int PassengerCount { get; set; }

    public int Capacity { get; set; } = 222;

    /// <summary>Boarded at the most recent exchange (for reporting to the Track Model).</summary>
    public int LastBoarded { get; set; }

    /// <summary>Alighted at the most recent exchange.</summary>
    public int LastAlighted { get; set; }

    /// <summary>Passengers left on the platform at the most recent exchange because
    /// the train was at capacity (waiting-to-board minus those who actually fit).
    /// Reported so the Track Model knows they did NOT board.</summary>
    public int LastLeftBehind { get; set; }

    /// <summary>True if the most recent exchange hit the capacity ceiling.</summary>
    public bool WasAtCapacity { get; set; }
    /// <summary>Id of the most recent passenger exchange (for the Track Model to dedupe).</summary>
    public System.Guid LastExchangeId { get; set; }

    /// <summary>Station (beacon name) of the most recent exchange.</summary>
    public string LastExchangeStation { get; set; } = string.Empty;

}
