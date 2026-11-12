namespace TrainControl.Contracts.Enums;

/// <summary>
/// How far a <see cref="Messages.TrainDispatchRequestMessage"/> (or a
/// <see cref="Messages.TrainRouteUpdateMessage"/>) got. Each stage is a separate fact; none of them is
/// a movement authorization.
/// </summary>
public enum TrainDispatchStage
{
    /// <summary>The Track Controller validated the request and delivered it to the Track Model. Not a placement.</summary>
    ForwardedToTrackModel = 0,

    /// <summary>
    /// The Track Model validated the placement, registered the train's route and delivered the activation
    /// to the Train Model. The train is not on the track yet: that happens when the Train Model reports
    /// its initial position.
    /// </summary>
    ActivationRequested,

    /// <summary>
    /// The Track Model received the Train Model's initial position report and occupies the start block.
    /// CTC still confirms the train itself from the start block's (anonymous) occupancy.
    /// </summary>
    Placed,

    /// <summary>The request was refused and placed nothing; see <see cref="Messages.TrainDispatchStatusMessage.IsRetryable"/>.</summary>
    Rejected,

    /// <summary>The Track Model accepted a route update for a train already on the track.</summary>
    RouteUpdated,

    /// <summary>
    /// The Track Model could not reconcile the train's reported position with the track (e.g. a switch not
    /// aligned for its path, a position beyond its route, or an inconsistent jump). The train's last
    /// occupancy is held, nothing is cleared, and further telemetry is not applied until it is retired.
    /// </summary>
    TrackingFault,

    /// <summary>The Track Model removed the train from the track and cleared its footprint.</summary>
    Retired,
}
