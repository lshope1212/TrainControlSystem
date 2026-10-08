using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;

namespace TrackController.Contracts.Messages;

/// <summary>Reports adjacent foreign blocks without granting command authority over them.</summary>
public sealed record WaysideNeighborObservation(
    string BlockId,
    [property: JsonPropertyName("fresh")] bool IsFresh,
    OccupancyState Occupancy,
    [property: JsonPropertyName("faulted")] bool IsFaulted,
    SignalState Signal,
    SwitchPosition Switch,
    CrossingState Crossing,
    [property: JsonPropertyName("reservedByCtc")] bool? IsReservedByCtc);
