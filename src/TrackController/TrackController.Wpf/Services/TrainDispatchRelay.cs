using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using TrackController.Contracts.Messages;
using TrainControl.Common.Communication;
using TrainControl.Common.Identity;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Wpf.Services;

/// <summary>
/// The Track Controller's part of physical dispatch: an intermediary between CTC and the Track Model,
/// at the integration layer. It never touches the PLC, movement evaluation or any authority.
/// </summary>
/// <remarks>
/// <para>
/// CTC sends every <see cref="TrainDispatchRequestMessage"/> (and <see cref="TrainRouteUpdateMessage"/>) to
/// the one Track Controller pipe; the dispatcher never needs to know which wayside owns the start block.
/// The relay checks the request's shape and, for a dispatch, that this deployment owns the start block and
/// currently reports it fresh, clear and open (the Track Controller owns maintenance closures). It then
/// forwards the request to the Track Model on its own sender, so a slow or absent Track Model never stalls
/// the controller's input pipe, and tells CTC what happened (<see cref="TrainDispatchStage.ForwardedToTrackModel"/>
/// or <see cref="TrainDispatchStage.Rejected"/>, with whether a retry can succeed).
/// </para>
/// <para>
/// The Track Model's own <see cref="TrainDispatchStatusMessage"/>s arrive on the controller's input pipe and
/// are relayed to CTC unchanged, in order, on the CTC publisher. Accepting or forwarding a dispatch grants
/// no movement authority: authority still comes only from a complete movement request the controller
/// evaluates against fresh occupancy.
/// </para>
/// </remarks>
public sealed class TrainDispatchRelay
{
    private const string ReportedBy = "Track Controller";

    private readonly Func<WaysideSnapshotMessage> _snapshot;
    private readonly string _trackModelPipe;
    private readonly Action<TrainDispatchStatusMessage> _toCtc;
    private readonly Action<string> _log;
    private readonly Channel<Forward> _forwards = Channel.CreateBounded<Forward>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    /// <param name="snapshot">The controller's latest wayside snapshot (ownership and measured block state).</param>
    /// <param name="trackModelPipe">The Track Model's inbound pipe.</param>
    /// <param name="toCtc">Queues a status on the ordered CTC publisher.</param>
    /// <param name="log">Operator-visible status line.</param>
    public TrainDispatchRelay(Func<WaysideSnapshotMessage> snapshot, string trackModelPipe, Action<TrainDispatchStatusMessage> toCtc, Action<string> log)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _trackModelPipe = trackModelPipe;
        _toCtc = toCtc ?? throw new ArgumentNullException(nameof(toCtc));
        _log = log ?? (_ => { });
    }

    /// <summary>True for the message types the relay handles instead of the controller runtime.</summary>
    public static bool Handles(string messageType) =>
        messageType is nameof(TrainDispatchRequestMessage) or nameof(TrainRouteUpdateMessage) or nameof(TrainDispatchStatusMessage);

    /// <summary>Handles one envelope from the controller's input pipe. Never blocks on another module.</summary>
    public void Receive(MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        switch (envelope.MessageType)
        {
            case nameof(TrainDispatchStatusMessage):
                RelayStatus(envelope);
                break;
            case nameof(TrainDispatchRequestMessage):
                if (Read<TrainDispatchRequestMessage>(envelope) is { } dispatch)
                    Validate(Identity.Of(dispatch), dispatch, ValidateDispatch(dispatch));
                break;
            case nameof(TrainRouteUpdateMessage):
                if (Read<TrainRouteUpdateMessage>(envelope) is { } route)
                    Validate(Identity.Of(route), route, ValidateRouteUpdate(route));
                break;
        }
    }

    /// <summary>Forwards accepted requests to the Track Model, in order, until cancelled.</summary>
    public async Task RunAsync(CancellationToken stop)
    {
        try
        {
            await foreach (var forward in _forwards.Reader.ReadAllAsync(stop))
            {
                bool delivered;
                try
                {
                    await NamedPipeTransport.SendAsync(_trackModelPipe, forward.Message, stop, connectTimeoutMilliseconds: 500);
                    delivered = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException || (ex is OperationCanceledException && !stop.IsCancellationRequested))
                {
                    delivered = false;
                }

                if (delivered)
                {
                    Report(forward.Identity, TrainDispatchStage.ForwardedToTrackModel, $"Forwarded to the Track Model for {(forward.Identity.IsRouteUpdate ? "its route update" : "physical placement")}; no movement authority is granted by this.");
                }
                else
                {
                    Report(forward.Identity, TrainDispatchStage.Rejected, "The Track Model is not reachable; nothing was placed. Retry when it is running.", retryable: true);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    private void Validate(Identity identity, object message, (string? Problem, bool Retryable) check)
    {
        if (check.Problem is { } problem)
        {
            Report(identity, TrainDispatchStage.Rejected, problem, check.Retryable);
            return;
        }

        if (!_forwards.Writer.TryWrite(new Forward(identity, message)))
        {
            Report(identity, TrainDispatchStage.Rejected, "The Track Controller is still forwarding earlier requests to an unresponsive Track Model; retry later.", retryable: true);
            return;
        }

        _log($"{(identity.IsRouteUpdate ? "Route update" : "Dispatch")} for train {identity.TrainId} ({identity.StartBlockId}) accepted; forwarding to the Track Model.");
    }

    /// <summary>Shape, ownership and measured safety of the start block. Grants nothing.</summary>
    private (string?, bool) ValidateDispatch(TrainDispatchRequestMessage request)
    {
        if (ValidateCommon(request.RequestId, request.TrainId, request.LineId, request.RouteBlockIds) is { } problem) return (problem, false);
        if (string.IsNullOrWhiteSpace(request.StartBlockId) || request.RouteBlockIds[0] != request.StartBlockId)
            return ($"The route must start with the start block '{request.StartBlockId}'.", false);

        var block = _snapshot().Blocks.FirstOrDefault(candidate => candidate.BlockId == request.StartBlockId);
        if (block is null)
            return ($"Block {request.StartBlockId} is not controlled by this Track Controller deployment; load the setup that owns it.", true);
        if (block.Maintenance == "Closed")
            return ($"Start block {block.BlockId} is closed for maintenance.", true);
        if (!block.IsFresh || block.IsFaulted)
            return ($"Start block {block.BlockId} feedback is unavailable or faulted.", true);
        if (block.Occupancy != OccupancyState.Clear)
            return ($"Start block {block.BlockId} is not clear ({block.Occupancy}).", true);
        return (null, false);
    }

    private static (string?, bool) ValidateRouteUpdate(TrainRouteUpdateMessage update) =>
        (ValidateCommon(update.RequestId, update.TrainId, update.LineId, update.RouteBlockIds), false);

    private static string? ValidateCommon(string requestId, string trainId, string lineId, List<string>? route)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128) return "A request ID of up to 128 characters is required.";
        if (!TrainIdConvention.IsDispatchable(trainId)) return $"'{trainId}' is not a dispatchable fleet train ID ({TrainIdConvention.DispatchableRangeText}).";
        if (string.IsNullOrWhiteSpace(lineId)) return "A line ID is required.";
        if (route is null || route.Count == 0 || route.Any(string.IsNullOrWhiteSpace)) return "An ordered route of block IDs is required.";
        return null;
    }

    private void RelayStatus(MessageEnvelope envelope)
    {
        if (Read<TrainDispatchStatusMessage>(envelope) is not { } status) return;
        _log($"Track Model: train {status.TrainId} {status.Stage}{(status.Detail.Length > 0 ? " — " + status.Detail : string.Empty)}");
        _toCtc(status);
    }

    private void Report(Identity identity, TrainDispatchStage stage, string detail, bool retryable = false)
    {
        Trace.WriteLine($"[Track Controller] {identity.RequestType} {identity.RequestId} train {identity.TrainId}: {stage} {detail}");
        if (stage == TrainDispatchStage.Rejected) _log($"{(identity.IsRouteUpdate ? "Route update" : "Dispatch")} for train {identity.TrainId} rejected: {detail}");
        _toCtc(new TrainDispatchStatusMessage
        {
            RequestId = identity.RequestId,
            RequestType = identity.RequestType,
            TrainId = identity.TrainId,
            LineId = identity.LineId,
            StartBlockId = identity.StartBlockId,
            Stage = stage,
            IsRetryable = retryable,
            Detail = detail,
            ReportedBy = ReportedBy,
            ReportedAtUtc = DateTimeOffset.UtcNow,
        });
    }

    private TMessage? Read<TMessage>(MessageEnvelope envelope)
        where TMessage : class
    {
        try
        {
            return MessageSerializer.DeserializePayload<TMessage>(envelope);
        }
        catch (JsonException ex)
        {
            // No request ID can be read, so no correlated answer is possible; CTC retries.
            _log($"Unreadable {envelope.MessageType}: {ex.Message}");
            return null;
        }
    }

    private sealed record Forward(Identity Identity, object Message);

    private sealed record Identity(string RequestId, string RequestType, string TrainId, string LineId, string StartBlockId)
    {
        public bool IsRouteUpdate => RequestType == nameof(TrainRouteUpdateMessage);

        public static Identity Of(TrainDispatchRequestMessage m) => new(m.RequestId ?? "", nameof(TrainDispatchRequestMessage), m.TrainId ?? "", m.LineId ?? "", m.StartBlockId ?? "");

        public static Identity Of(TrainRouteUpdateMessage m) => new(m.RequestId ?? "", nameof(TrainRouteUpdateMessage), m.TrainId ?? "", m.LineId ?? "", m.RouteBlockIds?.FirstOrDefault() ?? "");
    }
}
