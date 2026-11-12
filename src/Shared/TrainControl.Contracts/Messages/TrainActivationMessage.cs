using System.Text.Json.Serialization;

namespace TrainControl.Contracts.Messages;

/// <summary>
/// Puts a train in service (or retires it). Direction: Track Model -> Train Model.
/// </summary>
/// <remarks>
/// <para>
/// A command, not telemetry. On <see cref="Activate"/> the Train Model creates the train if it does
/// not exist, sets its cumulative <see cref="TrainStateMessage.PositionMeters"/> and speed to 0, keeps
/// its physical specification (length), assigns <see cref="LineId"/> for its roster, marks it active
/// and publishes an initial <see cref="TrainStateMessage"/> at once (even while the simulation clock is
/// paused), so the Track Model can occupy the start block.
/// </para>
/// <para>
/// The Train Model knows no track geometry: <see cref="StartBlockId"/> is for its log only and it never
/// derives a position from it. Repeating the activation of an operating train with the same
/// <see cref="DispatchRequestId"/> is idempotent (the current position is re-published; nothing is
/// reset). With <see cref="Activate"/> false the train is taken out of service.
/// </para>
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TrainActivationMessage
{
    /// <summary>The dispatch this activation belongs to; echoed on every position report.</summary>
    public string DispatchRequestId { get; set; } = string.Empty;

    /// <summary>Canonical three-digit train ID.</summary>
    public string TrainId { get; set; } = string.Empty;

    /// <summary>Line the train serves, e.g. "Green" (roster/display only).</summary>
    public string LineId { get; set; } = string.Empty;

    /// <summary>Diagnostic only: where the Track Model placed the train.</summary>
    public string StartBlockId { get; set; } = string.Empty;

    /// <summary>True to put the train in service at position 0; false to retire it.</summary>
    public bool Activate { get; set; } = true;

    /// <summary>Readable reason, e.g. why a train was retired.</summary>
    public string Reason { get; set; } = string.Empty;
}
