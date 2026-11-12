using System.Text.Json.Serialization;

namespace TrainControl.Contracts.Messages;

/// <summary>
/// The dispatcher re-planned a placed train's route. Direction: CTC -> Track Controller -> Track Model.
/// </summary>
/// <remarks>
/// <para>
/// The Track Model maps a train's cumulative position onto the route registered at placement. When CTC
/// changes the route of a train already on the track, it sends the new route from the block CTC
/// currently places the train in. The Track Model keeps everything the train already traversed (its
/// cumulative boundaries never change) and replaces only the route ahead of the train's front, after
/// checking that the new route continues from where the train physically is and uses valid connections
/// and switch branches. A route that cannot be reconciled is rejected and the old route stays in force.
/// </para>
/// <para>
/// Not a movement authorization. Answered with <see cref="TrainDispatchStatusMessage"/>
/// (<see cref="Enums.TrainDispatchStage.RouteUpdated"/> or <see cref="Enums.TrainDispatchStage.Rejected"/>),
/// correlated by <see cref="RequestId"/>; a retry reuses the ID.
/// </para>
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TrainRouteUpdateMessage
{
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Canonical three-digit train ID.</summary>
    public string TrainId { get; set; } = string.Empty;

    public string LineId { get; set; } = string.Empty;

    /// <summary>Ordered route from the block CTC currently places the train in.</summary>
    public List<string> RouteBlockIds { get; set; } = [];
}
