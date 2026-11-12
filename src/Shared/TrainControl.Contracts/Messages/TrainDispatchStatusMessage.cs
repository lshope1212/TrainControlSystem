using TrainControl.Contracts.Enums;

namespace TrainControl.Contracts.Messages;

/// <summary>
/// The outcome of one dispatch or route update, correlated by <see cref="RequestId"/>.
/// Direction: Track Model -> Track Controller -> CTC (relayed unchanged), and Track Controller -> CTC
/// for the Track Controller's own stage.
/// </summary>
/// <remarks>
/// <para>
/// Transport delivery, forwarding, Train Model activation and physical placement are separate facts
/// (<see cref="TrainDispatchStage"/>); none of them grants movement authority. CTC does not take the
/// train's location from this message: it confirms the train from the start block's anonymous
/// occupancy, exactly as before.
/// </para>
/// <para>
/// A receiver ignores a status whose <see cref="RequestId"/> is not a request it still waits on.
/// </para>
/// </remarks>
public sealed class TrainDispatchStatusMessage
{
    public string RequestId { get; set; } = string.Empty;

    /// <summary>"TrainDispatchRequestMessage" or "TrainRouteUpdateMessage" (empty for a fault report).</summary>
    public string RequestType { get; set; } = string.Empty;

    public string TrainId { get; set; } = string.Empty;

    public string LineId { get; set; } = string.Empty;

    public string StartBlockId { get; set; } = string.Empty;

    public TrainDispatchStage Stage { get; set; }

    /// <summary>
    /// For <see cref="TrainDispatchStage.Rejected"/>: whether the cause can clear by itself (an endpoint
    /// is unavailable, the start block is occupied or closed), so the sender should retry the same
    /// request later. False for a request that can never succeed as sent (unknown block, invalid route).
    /// </summary>
    public bool IsRetryable { get; set; }

    /// <summary>Readable explanation, e.g. the rejection reason.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>"Track Controller" or "Track Model".</summary>
    public string ReportedBy { get; set; } = string.Empty;

    /// <summary>Wall-clock time (UTC) the stage was decided.</summary>
    public DateTimeOffset ReportedAtUtc { get; set; }
}
