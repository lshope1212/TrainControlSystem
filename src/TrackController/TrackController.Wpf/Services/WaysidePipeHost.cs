using System.IO;
using TrackController.Contracts.Messages;
using TrackController.Core.Interfaces;
using TrackController.Runtime;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Wpf.Services;

/// <summary>One production receiver/publisher implementation; tests only substitute endpoint addresses.</summary>
public sealed class WaysidePipeHost : IAsyncDisposable
{
    private readonly IWaysideRuntime _runtime;

    public WaysideSnapshotMessage LatestSnapshot => _runtime.LatestSnapshot;

    /// <summary>Reads current runtime state before allowing deployment or placement changes.</summary>
    public Task<WaysideSnapshotMessage> PollForSetupAsync(CancellationToken cancellationToken) => _runtime.PollAsync(cancellationToken);

    /// <summary>Returns the applied controller and execution target for a block.</summary>
    public string GetOwnerDescription(string blockId) => _runtime is TerritoryRuntimeRouter router ? router.GetOwnerDescription(blockId) : "Local PC preview";

    private readonly WaysideEndpoints _endpoints;
    private readonly CancellationTokenSource _stop = new();
    private Task[]? _workers;
    private readonly HashSet<string> _knownTrainLabels = [];

    public event Action<string>? StatusChanged;

    public event Action<string>? InputReceived;

    /// <summary>Connects a runtime to the production named-pipe receiver and output publishers.</summary>
    public WaysidePipeHost(ITrackControllerService controller, WaysideEndpoints? endpoints = null) : this(new LocalWaysideRuntime(controller), endpoints)
    {
    }

    /// <summary>Connects a runtime to the production named-pipe receiver and output publishers.</summary>
    public WaysidePipeHost(IWaysideRuntime runtime, WaysideEndpoints? endpoints = null)
    {
        _runtime = runtime;
        _endpoints = endpoints ?? WaysideEndpoints.Live;
    }

    /// <summary>Starts the production input receiver and independent CTC and Track Model publishers.</summary>
    public void Start()
    {
        if (_workers is not null)
        {
            throw new InvalidOperationException("Pipe host already started.");
        }

        _workers = [NamedPipeTransport.ListenAsync(_endpoints.Input, HandleAsync, ex => InputReceived?.Invoke($"Input connection: {ex.Message}"), _stop.Token), PublishAsync(false, _stop.Token), PublishAsync(true, _stop.Token)];
    }

    private async Task HandleAsync(MessageEnvelope envelope)
    {
        object result = await _runtime.ReceiveAsync(envelope, _stop.Token).ConfigureAwait(false);
        InputReceived?.Invoke(result is WaysideAcknowledgementMessage ack ? ack.Detail : "Status requested.");
        // Application results use the normal output pipe, not a test-only callback or duplex protocol.
        await TrySendAsync(_endpoints.TrackModelOutput, result, _stop.Token).ConfigureAwait(false);
    }

    private async Task PublishAsync(bool ctc, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var snapshot = await _runtime.PollAsync(stop).ConfigureAwait(false);
                string destination = ctc ? _endpoints.CtcOutput : _endpoints.TrackModelOutput;
                bool delivered = true;
                foreach (object message in ctc ? CreateCtcMessages(snapshot) : new object[]
                {
                    snapshot
                }

                )
                {
                    if (!await TrySendAsync(destination, message, stop).ConfigureAwait(false))
                    {
                        delivered = false;
                        break;
                    }
                }

                StatusChanged?.Invoke($"{(ctc ? "CTC" : "Track Model")}: {(delivered ? "sent" : "unavailable")}");
                await Task.Delay(500, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return;
        }
    }

    private IEnumerable<object> CreateCtcMessages(WaysideSnapshotMessage snapshot)
    {
        // Match the team's current CTC receiver contracts; reported equipment is never replaced by commands.
        foreach (var blockStatus in snapshot.Blocks)
        {
            yield return new BlockStatusMessage
            {
                BlockId = blockStatus.BlockId,
                Occupancy = blockStatus.IsFresh && !blockStatus.IsFaulted ? blockStatus.Occupancy : OccupancyState.Unknown,
                Signal = blockStatus.IsFresh ? blockStatus.ReportedSignal : SignalState.Unknown,
                Switch = blockStatus.IsFresh ? blockStatus.ReportedSwitch : SwitchPosition.Unknown,
                Crossing = blockStatus.IsFresh ? blockStatus.ReportedCrossing : CrossingState.Unknown
            };
        }

        var current = snapshot.Authorizations.Where(authorization => !string.IsNullOrWhiteSpace(authorization.TrainId)).GroupBy(authorization => authorization.TrainId).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (string label in current.Keys)
        {
            _knownTrainLabels.Add(label);
        }

        foreach (string label in _knownTrainLabels)
        {
            // An absent/cancelled request revokes the previous indication. Ambiguous labels fail closed.
            var authorization = current.TryGetValue(label, out var rows) && rows.Length == 1 ? rows[0] : null;
            yield return new TrainAuthorizationStatusMessage
            {
                TrainId = label,
                AuthorizedSpeedMetersPerSecond = authorization?.AuthorizedSpeedMetersPerSecond ?? 0,
                AuthorizedAuthorityMeters = authorization?.AuthorizedAuthorityMeters ?? 0
            };
        }
    }

    private static async Task<bool> TrySendAsync(string pipe, object message, CancellationToken stop)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
            deadline.CancelAfter(300);
            await NamedPipeTransport.SendAsync(pipe, message, deadline.Token, connectTimeoutMilliseconds: 250).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Stops owned background operations and releases connections before returning.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_workers is not null)
        {
            await Task.WhenAll(_workers);
        }

        await _runtime.DisposeAsync();
        _stop.Dispose();
    }
}
