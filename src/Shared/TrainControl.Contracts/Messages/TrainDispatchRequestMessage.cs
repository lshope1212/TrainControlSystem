using System.Text.Json.Serialization;

namespace TrainControl.Contracts.Messages;

/// <summary>
/// Physical dispatch: CTC asks for a train to be placed in service at its start block, to run the
/// given ordered route. Direction: CTC -> Track Controller -> Track Model.
/// </summary>
/// <remarks>
/// <para>
/// This is placement only, never movement authorization: the Track Controller forwards it to the
/// Track Model and grants nothing because of it, and the Track Model places the train with zero speed
/// and zero authority. Movement is authorized separately, by <see cref="MovementSuggestionMessage"/>,
/// once CTC has detected the train in its start block from anonymous occupancy.
/// </para>
/// <para>
/// CTC sends the request to the single Track Controller pipe; the Track Controller does not need the
/// dispatcher to know which wayside owns the start block. The Track Model validates the train ID,
/// line, start block and route against its topology and current track state, registers the train's
/// route, and asks the Train Model to activate the train at cumulative position 0.
/// </para>
/// <para>
/// Idempotent: a retry carries the same <see cref="RequestId"/> and never creates or resets a second
/// train. Every stage is reported back with <see cref="TrainDispatchStatusMessage"/> correlated by
/// <see cref="RequestId"/>. Physical placement is confirmed only by the start block's occupancy.
/// </para>
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TrainDispatchRequestMessage
{
    /// <summary>Unique per placement attempt; retries of the same placement reuse it.</summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Canonical three-digit train ID (see <c>TrainIdConvention</c>).</summary>
    public string TrainId { get; set; } = string.Empty;

    /// <summary>Track Model line ID of the route, e.g. "Green".</summary>
    public string LineId { get; set; } = string.Empty;

    /// <summary>Block the train's front starts at (the entry of this block is cumulative position 0).</summary>
    public string StartBlockId { get; set; } = string.Empty;

    /// <summary>Ordered blocks the train is planned to traverse, starting with <see cref="StartBlockId"/>.</summary>
    public List<string> RouteBlockIds { get; set; } = [];
}
