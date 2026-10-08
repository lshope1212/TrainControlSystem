using System.Text.Json;
using TrackController.Contracts.Messages;
using TrackController.Core.Models;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Messages;

namespace TrackController.Runtime;

/// <summary>Block ownership routes one logical module's inputs to PC or Pi; it does not vote or duplicate control.</summary>
public sealed class TerritoryRuntimeRouter : IWaysideRuntime
{
    /// <summary>Associates an owned territory with its runtime and execution target.</summary>
    public sealed record Entry(WaysideConfigurationMessage Configuration, string Execution, IWaysideRuntime Runtime, string? Name = null);

    private readonly Entry[] _entries;
    private readonly Dictionary<string, Entry> _owners;
    private readonly Dictionary<Entry, HashSet<string>> _observed;
    private readonly string _name;
    private readonly SemaphoreSlim _operations = new(1, 1);

    public WaysideSnapshotMessage LatestSnapshot { get; private set; }

    public string InitialProgramSource { get; init; } = "";

    public string Description => string.Join(" · ", _entries.Select(entry => $"{entry.Name ?? entry.Configuration.WaysideId}: {(entry.Execution == "Pi" ? "Pi 5" : "PC")} [{string.Join(", ", WaysideConfiguration.GetOwnedIds(entry.Configuration))}]"));

    /// <summary>Returns the applied controller and execution target for a block.</summary>
    public string GetOwnerDescription(string blockId) => _owners.TryGetValue(blockId, out var owner) ? $"{owner.Name ?? owner.Configuration.WaysideId} · {(owner.Execution == "Pi" ? "Pi 5" : "Local PC")}" : "Unassigned";

    /// <summary>Validates unique ownership and connects each territory to its assigned runtime.</summary>
    public TerritoryRuntimeRouter(string name, IEnumerable<Entry> entries)
    {
        _name = name;
        _entries = entries.ToArray();
        if (_entries.Length == 0)
        {
            throw new ArgumentException("No territories configured.");
        }

        _owners = new(StringComparer.Ordinal);
        _observed = _entries.ToDictionary(entry => entry, entry => WaysideConfiguration.GetOwnedIds(entry.Configuration).Union(WaysideConfiguration.GetNeighborIds(entry.Configuration)).ToHashSet(StringComparer.Ordinal));
        foreach (var entry in _entries)
        {
            foreach (string id in WaysideConfiguration.GetOwnedIds(entry.Configuration))
            {
                if (!_owners.TryAdd(id, entry))
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
            if (message.MessageType == nameof(WaysideSnapshotRequest))
            {
                return await PollAllAsync(cancellationToken);
            }

            if (message.MessageType == nameof(WaysideFeedbackMessage))
            {
                var feedback = MessageSerializer.DeserializePayload<WaysideFeedbackMessage>(message);
                if (feedback.Blocks is null || feedback.Blocks.Any(feedback => feedback is null) || feedback.Blocks.Select(feedback => feedback.BlockId).Distinct().Count() != feedback.Blocks.Count)
                {
                    throw new ArgumentException("Feedback must have distinct blocks.");
                }
                // Validate every owner before delivering any subset.
                foreach (var block in feedback.Blocks)
                {
                    Owner(block.BlockId);
                }

                var replies = await Task.WhenAll(_entries.Select(entry => (Entry: entry, Blocks: feedback.Blocks.Where(feedback => _observed[entry].Contains(feedback.BlockId)).ToList())).Where(part => part.Blocks.Count > 0).Select(part => part.Entry.Runtime.ReceiveAsync(RuntimeMessages.CreateEnvelope(new WaysideFeedbackMessage { Blocks = part.Blocks }), cancellationToken)));
                return Receipt(message.MessageType, replies);
            }

            if (message.MessageType is nameof(BlockStatusMessage) or nameof(TrackStateMessage))
            {
                string id = message.MessageType == nameof(BlockStatusMessage) ? MessageSerializer.DeserializePayload<BlockStatusMessage>(message).BlockId : MessageSerializer.DeserializePayload<TrackStateMessage>(message).BlockId;
                Owner(id);
                return Receipt(message.MessageType, await Task.WhenAll(_entries.Where(entry => _observed[entry].Contains(id)).Select(entry => entry.Runtime.ReceiveAsync(message, cancellationToken))));
            }

            Entry? target = message.MessageType switch
            {
                nameof(WaysideMovementRequestMessage) => MovementOwner(MessageSerializer.DeserializePayload<WaysideMovementRequestMessage>(message)),
                nameof(SwitchPositionRequestMessage) => Owner(MessageSerializer.DeserializePayload<SwitchPositionRequestMessage>(message).BlockId),
                nameof(MaintenanceRequestMessage) => Owner(MessageSerializer.DeserializePayload<MaintenanceRequestMessage>(message).BlockId),
                nameof(WaysideManualCommandMessage) => Owner(MessageSerializer.DeserializePayload<WaysideManualCommandMessage>(message).BlockId),
                _ => null
            };
            if (target is not null)
            {
                return await target.Runtime.ReceiveAsync(message, cancellationToken);
            }

            if (message.MessageType == nameof(WaysideConfigurationMessage))
            {
                throw new ArgumentException("Use Load SW/HW deployment to change routed territory ownership.");
            }

            if (message.MessageType == nameof(WaysideControllerModeMessage) && !MessageSerializer.DeserializePayload<WaysideControllerModeMessage>(message).IsHardwareMode)
            {
                throw new ArgumentException("A routed deployment uses the same PLC program on PC and Pi. Software-only logic is available in the local demo.");
            }

            if (message.MessageType is not (nameof(WaysideSimulationMessage) or nameof(WaysideControllerModeMessage) or nameof(WaysideMaintenanceModeMessage) or nameof(WaysidePlcProgramMessage)))
            {
                throw new ArgumentException($"Unsupported routed input: {message.MessageType}. Movement must identify its target and route.");
            }

            var results = await Task.WhenAll(_entries.Select(entry => entry.Runtime.ReceiveAsync(message, cancellationToken)));
            var result = Receipt(message.MessageType, results);
            if (!result.IsAccepted && message.MessageType == nameof(WaysideSimulationMessage) && !MessageSerializer.DeserializePayload<WaysideSimulationMessage>(message).IsPaused)
            {
                await Task.WhenAll(_entries.Select(entry => entry.Runtime.ReceiveAsync(RuntimeMessages.CreateEnvelope(new WaysideSimulationMessage { IsPaused = true }), cancellationToken)));
            }

            return result;
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException)
        {
            return RuntimeMessages.CreateRejection(message.MessageType, ex.Message);
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

    private Entry Owner(string id) => id is not null && _owners.TryGetValue(id, out var owner) ? owner : throw new ArgumentException($"Block '{id}' has no owner in this deployment.");

    private Entry MovementOwner(WaysideMovementRequestMessage request)
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

        if (request.EntryBlockId is not null && !_observed[owner].Contains(request.EntryBlockId))
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
        var snapshots = _entries.Select(entry => entry.Runtime.LatestSnapshot.ValidUntilUtc > DateTimeOffset.UtcNow ? entry.Runtime.LatestSnapshot : UnavailableOutput.CreateStopSnapshot(entry.Runtime.LatestSnapshot, $"{entry.Configuration.WaysideId} output expired.")).ToArray();
        return new(_name, snapshots[0].LineName, snapshots.Min(snapshot => snapshot.EvaluatedAtUtc), snapshots.Min(snapshot => snapshot.ValidUntilUtc), snapshots.Max(snapshot => snapshot.SimulationSeconds), snapshots.All(snapshot => snapshot.IsPaused), snapshots[0].SpeedMultiplier, true, snapshots.Any(snapshot => snapshot.IsMaintenanceMode), string.Join(" / ", snapshots.Select(snapshot => snapshot.PlcName).Distinct()), string.Join(" · ", snapshots.Select(snapshot => $"{snapshot.WaysideId}: {snapshot.PlcStatus}")), snapshots.SelectMany(snapshot => snapshot.Blocks).ToArray(), snapshots.SelectMany(snapshot => snapshot.Authorizations).ToArray());
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
