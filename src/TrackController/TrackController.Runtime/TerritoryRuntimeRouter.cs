using System.Text.Json;
using TrackController.Contracts.Messages;
using TrackController.Core.Models;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Messages;

namespace TrackController.Runtime;

/// <summary>Block ownership routes one logical module's inputs to PC or Pi; it does not vote or duplicate control.</summary>
public sealed class TerritoryRuntimeRouter : IWaysideRuntime
{
    private readonly (WaysideConfigurationMessage Configuration, string Execution, IWaysideRuntime Runtime, string? Name, HashSet<string> Observed)[] _entries;
    private readonly Dictionary<string, int> _owners;
    private readonly string _name;
    private readonly SemaphoreSlim _operations = new(1, 1);

    public WaysideSnapshotMessage LatestSnapshot { get; private set; }

    /// <summary>Returns the applied controller and execution target for a block.</summary>
    public string GetOwnerDescription(string blockId) => _owners.TryGetValue(blockId, out int owner) ? $"{_entries[owner].Name ?? _entries[owner].Configuration.WaysideId} · {(_entries[owner].Execution == "Pi" ? "Pi 5" : "Local PC")}" : "Unassigned";

    /// <summary>Validates unique ownership and connects each territory to its assigned runtime.</summary>
    public TerritoryRuntimeRouter(string name, IEnumerable<(WaysideConfigurationMessage Configuration, string Execution, IWaysideRuntime Runtime, string? Name)> entries)
    {
        _name = name;
        _entries = entries.Select(entry => (entry.Configuration, entry.Execution, entry.Runtime, entry.Name,
            WaysideConfiguration.GetOwnedIds(entry.Configuration).Union(WaysideConfiguration.GetNeighborIds(entry.Configuration)).ToHashSet(StringComparer.Ordinal))).ToArray();
        if (_entries.Length == 0)
        {
            throw new ArgumentException("No territories configured.");
        }

        _owners = new(StringComparer.Ordinal);
        for (int owner = 0; owner < _entries.Length; owner++)
        {
            foreach (string id in WaysideConfiguration.GetOwnedIds(_entries[owner].Configuration))
            {
                if (!_owners.TryAdd(id, owner))
                {
                    throw new ArgumentException($"Block {id} has more than one owner.");
                }
            }
        }

        LatestSnapshot = Combine();
    }

    /// <summary>Handles an application message through the selected runtime and returns its result.</summary>
    public async Task<object> ReceiveAsync(MessageEnvelope message, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            if (message.MessageType == "GetSnapshot")
            {
                return await PollAllAsync(cancellationToken);
            }

            if (message.MessageType == "TrackModelBlockStates")
            {
                var feedback = MessageSerializer.ReadValue<List<TrackModelBlockStateMessage>>(message, "blocks");
                if (feedback.Any(feedback => feedback is null) || feedback.Select(feedback => feedback.BlockId).Distinct().Count() != feedback.Count)
                {
                    throw new ArgumentException("Feedback must have distinct blocks.");
                }
                // Validate every owner before delivering any subset.
                foreach (var block in feedback)
                {
                    Owner(block.BlockId);
                }

                var replies = await Task.WhenAll(_entries.Select(entry => (entry.Runtime, Blocks: feedback.Where(feedback => entry.Observed.Contains(feedback.BlockId)).ToList())).Where(part => part.Blocks.Count > 0).Select(part => part.Runtime.ReceiveAsync(MessageSerializer.CreateEnvelope("TrackModelBlockStates", new { blocks = part.Blocks }), cancellationToken)));
                return Receipt(message.MessageType, replies);
            }

            if (message.MessageType is nameof(TrackModelBlockStateMessage) or nameof(BlockStatusMessage))
            {
                string id = message.MessageType == nameof(TrackModelBlockStateMessage)
                    ? MessageSerializer.DeserializePayload<TrackModelBlockStateMessage>(message).BlockId
                    : MessageSerializer.DeserializePayload<BlockStatusMessage>(message).BlockId;
                Owner(id);
                return Receipt(message.MessageType, await Task.WhenAll(_entries.Where(entry => entry.Observed.Contains(id)).Select(entry => entry.Runtime.ReceiveAsync(message, cancellationToken))));
            }

            if (message.MessageType == "LoadPlc")
            {
                string? territoryId = message.Payload.TryGetProperty("territoryId", out var targetId) ? targetId.GetString() : null;
                if (territoryId is not null)
                {
                    var territory = _entries.SingleOrDefault(entry => entry.Configuration.WaysideId == territoryId);
                    if (territory.Runtime is null) throw new ArgumentException("Unknown PLC territory.");
                    return await territory.Runtime.ReceiveAsync(message, cancellationToken);
                }
            }

            int? target = message.MessageType switch
            {
                nameof(MovementSuggestionMessage) => MovementOwner(MessageSerializer.DeserializePayload<MovementSuggestionMessage>(message)),
                nameof(SwitchPositionRequestMessage) => Owner(MessageSerializer.DeserializePayload<SwitchPositionRequestMessage>(message).BlockId),
                nameof(MaintenanceRequestMessage) => Owner(MessageSerializer.DeserializePayload<MaintenanceRequestMessage>(message).BlockId),
                "ApplyManual" => Owner(MessageSerializer.ReadValue<string>(message, "blockId")),
                _ => null
            };
            if (target is not null)
            {
                return await _entries[target.Value].Runtime.ReceiveAsync(message, cancellationToken);
            }

            if (message.MessageType == nameof(WaysideConfigurationMessage))
            {
                throw new ArgumentException("Use Load SW/HW deployment to change routed territory ownership.");
            }

            if (message.MessageType == "SetControllerMode" && !MessageSerializer.ReadValue<bool>(message, "hardwareMode"))
            {
                throw new ArgumentException("A routed deployment uses PLC logic on both PC and Pi. Software-only logic is available in the local demo.");
            }

            if (message.MessageType is not ("SetSimulation" or "SetControllerMode" or "SetMaintenanceMode" or "LoadPlc"))
            {
                throw new ArgumentException($"Unsupported routed input: {message.MessageType}. Movement must identify its target and route.");
            }

            var results = await Task.WhenAll(_entries.Select(entry => entry.Runtime.ReceiveAsync(message, cancellationToken)));
            var result = Receipt(message.MessageType, results);
            if (!result.IsAccepted && message.MessageType == "SetSimulation" && !MessageSerializer.ReadValue<bool>(message, "paused"))
            {
                await Task.WhenAll(_entries.Select(entry => entry.Runtime.ReceiveAsync(MessageSerializer.CreateEnvelope("SetSimulation", new { paused = true, speedMultiplier = 1 }), cancellationToken)));
            }

            return result;
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException)
        {
            return new WaysideAcknowledgementMessage(false, ex.Message, message.MessageType);
        }
        finally
        {
            LatestSnapshot = Combine();
            _operations.Release();
        }
    }

    /// <summary>Refreshes runtime outputs so timeouts apply even when no new input arrives.</summary>
    public async Task<WaysideSnapshotMessage> PollAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            return await PollAllAsync(cancellationToken);
        }
        finally
        {
            _operations.Release();
        }
    }

    private async Task<WaysideSnapshotMessage> PollAllAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(_entries.Select(entry => entry.Runtime.PollAsync(cancellationToken)));
        // A slow/unavailable Pi must not consume the local territory's entire output lease.
        foreach (var entry in _entries.Where(entry => entry.Execution == "Local"))
        {
            await entry.Runtime.PollAsync(cancellationToken);
        }

        return LatestSnapshot = Combine();
    }

    private int Owner(string id) => id is not null && _owners.TryGetValue(id, out int owner) ? owner : throw new ArgumentException($"Block '{id}' has no owner in this deployment.");

    private int MovementOwner(MovementSuggestionMessage request)
    {
        var owner = Owner(request.TargetBlockId);
        if (request.PathBlockIds is null)
        {
            throw new ArgumentException("Movement route is missing.");
        }

        if (request.PathBlockIds.Any(id => Owner(id) != owner))
        {
            throw new ArgumentException("Movement cannot cross a territory boundary until handoff coordination is implemented. Authority must end within its owner.");
        }

        if (request.EntryBlockId is not null && !_entries[owner].Observed.Contains(request.EntryBlockId))
        {
            throw new ArgumentException("Movement entry must be in this territory or an adjacent observed block.");
        }

        return owner;
    }

    private static WaysideAcknowledgementMessage Receipt(string type, object[] replies)
    {
        var errors = replies.OfType<WaysideAcknowledgementMessage>().Where(acknowledgement => !acknowledgement.IsAccepted).Select(acknowledgement => acknowledgement.Detail).ToArray();
        return new(errors.Length == 0, errors.Length == 0 ? "Delivered to the assigned controller(s). Inspect their outputs." : string.Join("; ", errors), type);
    }

    private WaysideSnapshotMessage Combine()
    {
        var snapshots = _entries.Select(entry => entry.Runtime.LatestSnapshot.ValidUntilUtc > DateTimeOffset.UtcNow ? entry.Runtime.LatestSnapshot : entry.Runtime.LatestSnapshot.CreateStopSnapshot($"{entry.Configuration.WaysideId} output expired.")).ToArray();
        return new(_name, snapshots[0].LineName, snapshots.Min(snapshot => snapshot.EvaluatedAtUtc), snapshots.Min(snapshot => snapshot.ValidUntilUtc), snapshots.Max(snapshot => snapshot.SimulationSeconds), snapshots.All(snapshot => snapshot.IsPaused), snapshots[0].SpeedMultiplier, true, snapshots.Any(snapshot => snapshot.IsMaintenanceMode), string.Join(" / ", snapshots.Select(snapshot => snapshot.PlcName).Distinct()), string.Join(" · ", snapshots.Select(snapshot => $"{snapshot.WaysideId} ({snapshot.PlcName}): {snapshot.PlcStatus}")), snapshots.SelectMany(snapshot => snapshot.Blocks).ToArray());
    }

    /// <summary>Stops owned background operations and releases connections before returning.</summary>
    public async ValueTask DisposeAsync()
    {
        await _operations.WaitAsync();
        try
        {
            foreach (var entry in _entries)
            {
                await entry.Runtime.DisposeAsync();
            }
        }
        finally
        {
            _operations.Release();
        }
    }
}
