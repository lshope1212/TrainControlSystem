namespace TrainControl.Contracts.Messages;

/// <summary>
/// A train's physical motion state, published by the Train Model after every physics tick (0.1 s of
/// simulated time) and once immediately at activation. Direction: Train Model -> Track Model.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PositionMeters"/> is the cumulative distance the FRONT of the train has travelled since
/// it was activated: 0 at activation, integrated from velocity by the Train Model's physics, in meters.
/// It is not a block number, not a map coordinate, not a distance within the current block and is never
/// reset at a block boundary. It can decrease only by genuine backward motion (e.g. rolling back on a
/// grade). The Train Model knows no track geometry; the Track Model maps the position onto the route it
/// registered at placement, using <see cref="TrainLengthMeters"/> for the train's footprint. No block ID
/// is ever carried here.
/// </para>
/// <para>
/// <see cref="Sequence"/> is the Train Model's physics tick count: it increases by one per 0.1 s of
/// simulated time, so a receiver can order reports, drop stale ones and bound how far the train can
/// have moved between two reports. Reports may be coalesced in transport (only the newest per train is
/// kept when the receiver is slower); because the position is cumulative, nothing is lost: the receiver
/// reconstructs every boundary crossed between two reports, in order.
/// </para>
/// </remarks>
public class TrainStateMessage
{
    public string TrainId { get; set; } = string.Empty;

    /// <summary>Cumulative distance of the train's front since activation, meters.</summary>
    public double PositionMeters { get; set; }

    public double SpeedMetersPerSecond { get; set; }

    public double AccelerationMetersPerSecondSquared { get; set; }

    /// <summary>Total consist length from the Train Model's specification, meters.</summary>
    public double TrainLengthMeters { get; set; }

    /// <summary>Physics tick count of this report (monotonic within a Train Model run); 0 = unordered legacy sender.</summary>
    public long Sequence { get; set; }

    /// <summary>The activation (<see cref="TrainActivationMessage.DispatchRequestId"/>) this train is running under; empty when none.</summary>
    public string DispatchRequestId { get; set; } = string.Empty;

    /// <summary>Passengers on board (informational).</summary>
    public int PassengerCount { get; set; }
}
