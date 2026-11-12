using TrackController.Contracts.Messages;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackController.Runtime;

public sealed partial class TerritoryRuntimeRouter
{
    private readonly Dictionary<(string Line, string Id), (MovementSuggestionMessage Request, double Meters)> _issued = new();
    private readonly Dictionary<(string Line, string Id), WaysideContinuation> _transfers = new();
    private readonly Dictionary<string, DateTimeOffset> _feedbackTimes = new(StringComparer.Ordinal);

    private async Task<object> RouteMovementAsync(MovementSuggestionMessage request, CancellationToken cancellationToken)
    {
        int owner = Owner(request.TargetBlockId);
        var configuration = _entries[owner].Configuration;
        var key = (configuration.LineName, request.RequestId);
        if (request.PathBlockIds is null) throw new ArgumentException("Movement route is missing.");
        foreach (string id in request.PathBlockIds.Append(request.EntryBlockId).Where(id => id is not null)!)
        {
            if (_entries[Owner(id)].Configuration.LineName != configuration.LineName)
                throw new ArgumentException("Movement cannot cross a territory boundary onto a different line.");
        }
        if (!configuration.CoordinatedRoutes)
        {
            if (request.PathBlockIds.Any(id => Owner(id) != owner))
                throw new ArgumentException("This deployment has not enabled coordinated territory routes.");
            return await _entries[owner].Runtime.ReceiveAsync(MessageSerializer.CreateEnvelope(nameof(MovementSuggestionMessage), request), cancellationToken);
        }

        // Every controller on this line sees the same route and occupancy transitions. Each retains
        // its own PLC, maintenance/interlocking state and sole ownership of its physical outputs.
        var participants = _entries.Where(entry => entry.Configuration.LineName == configuration.LineName).ToArray();
        var envelope = MessageSerializer.CreateEnvelope("CoordinateMovement", request);
        var replies = await Task.WhenAll(participants.Select(entry => entry.Runtime.ReceiveAsync(envelope, cancellationToken)));
        var receipt = Receipt(nameof(MovementSuggestionMessage), replies);
        if (!receipt.IsAccepted && request.IsActive)
        {
            // A partial delivery cannot become a latent grant if the failed participant reconnects.
            var cancel = MessageSerializer.DeserializePayload<MovementSuggestionMessage>(envelope);
            cancel.IsActive = false;
            await Task.WhenAll(participants.Select(entry => entry.Runtime.ReceiveAsync(MessageSerializer.CreateEnvelope("CoordinateMovement", cancel), cancellationToken)));
        }
        if (!request.IsActive || !receipt.IsAccepted)
        {
            _issued.Remove(key);
            _transfers.Remove(key);
        }
        else if (_issued.TryGetValue(key, out var held)
            && (held.Request.TrainId != request.TrainId || held.Request.SuggestedAuthorityMeters != request.SuggestedAuthorityMeters
                || (held.Request.SuggestedSpeedMetersPerSecond <= 0 && request.SuggestedSpeedMetersPerSecond > 0)))
        {
            _issued.Remove(key);
        }
        if (_issued.TryGetValue(key, out var retained))
            _issued[key] = (request, retained.Meters);
        return receipt with
        {
            Detail = receipt.IsAccepted ? receipt.Detail : $"Target block {request.TargetBlockId}: {receipt.Detail}",
            IsStagedSuccessor = receipt.IsAccepted && replies.OfType<WaysideAcknowledgementMessage>().All(reply => reply.IsStagedSuccessor)
        };
    }

    private void ObserveTransitions(IReadOnlyList<TrackModelBlockStateMessage> feedback)
    {
        Dictionary<string, WaysideBlockStatus>? published = null;
        foreach (var measurement in feedback)
        {
            if (_feedbackTimes.TryGetValue(measurement.BlockId, out var previousTime) && measurement.ObservedAtUtc <= previousTime) continue;
            _feedbackTimes[measurement.BlockId] = measurement.ObservedAtUtc;
            // One lookup table per batch, not a scan of every published block per measurement (first match, as before).
            published ??= LatestSnapshot.Blocks.GroupBy(block => block.BlockId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var before = published.GetValueOrDefault(measurement.BlockId);
            if (before is not { Occupancy: OccupancyState.Clear, IsFresh: true, Continuation: { } continuation }
                || measurement.Occupancy != OccupancyState.Occupied || measurement.IsFaulted || measurement.IsClosed
                || LatestSnapshot.ValidUntilUtc <= DateTimeOffset.UtcNow) continue;
            // Only a previously PUBLISHED joint grant can cross the boundary. An individual core's
            // more permissive assessment must never resurrect a jointly withheld movement.
            _transfers[(_entries[Owner(measurement.BlockId)].Configuration.LineName, continuation.SuccessorRequestId)] = continuation;
        }
    }

    private WaysideBlockStatus[] CombineRoutes(WaysideSnapshotMessage[] snapshots)
    {
        var blocks = snapshots.SelectMany(snapshot => snapshot.Blocks).ToDictionary(block => block.BlockId, StringComparer.Ordinal);
        foreach (var original in blocks.Values.ToArray())
        {
            int owner = Owner(original.BlockId);
            if (!_entries[owner].Configuration.CoordinatedRoutes || original.Request is not { } request) continue;
            var key = (_entries[owner].Configuration.LineName, request.RequestId);
            var participants = Enumerable.Range(0, _entries.Length)
                .Where(index => _entries[index].Configuration.LineName == _entries[owner].Configuration.LineName).ToArray();
            var checks = participants.Select(index => snapshots[index].RouteAssessments
                .FirstOrDefault(check => check.Request?.RequestId == request.RequestId && check.BlockId == original.BlockId)).ToArray();
            double authority = checks.Any(check => check is null) ? 0 : checks.Min(check => check!.AuthorizedAuthorityMeters);
            double speed = checks.Any(check => check is null) ? 0 : checks.Min(check => check!.AuthorizedSpeedMetersPerSecond);
            string reason = checks.FirstOrDefault(check => check is null || check.AuthorizedAuthorityMeters <= 0)?.MovementReason
                ?? "A participating wayside has no current route approval.";
            string authorityLimit = "Authority is shorter than the CTC request.";
            string speedLimit = "Speed is lower than the CTC suggestion.";
            if (request.PredecessorRequestId is not null)
            {
                if (!_transfers.TryGetValue(key, out var transfer))
                {
                    authority = 0;
                    reason = "No jointly authorized block transition was observed.";
                }
                else
                {
                    if (transfer.AuthorizedAuthorityMeters < authority)
                        authorityLimit = "Authority is capped at the endpoint carried over from the previous block.";
                    if (transfer.AuthorizedSpeedMetersPerSecond < speed)
                        speedLimit = "Speed is capped at the limit carried over from the previous block.";
                    authority = Math.Min(authority, transfer.AuthorizedAuthorityMeters);
                    speed = Math.Min(speed, transfer.AuthorizedSpeedMetersPerSecond);
                }
            }
            if (_issued.TryGetValue(key, out var held))
            {
                if (authority + 1e-6 < held.Meters)
                {
                    authority = 0;
                    reason = "A wayside withdrew part of the issued route; authority is zero until the full grant is safe again.";
                }
                else
                {
                    if (held.Meters < authority)
                        authorityLimit = "Authority is held at the endpoint already issued for this block.";
                    authority = held.Meters;
                }
            }
            if (speed <= 0) authority = 0;
            if (authority <= 0) speed = 0;
            else _issued.TryAdd(key, (request, authority));
            string status = "Withheld";
            if (authority > 0)
            {
                bool authorityRestricted = authority < request.SuggestedAuthorityMeters;
                bool speedRestricted = speed < request.SuggestedSpeedMetersPerSecond;
                status = authorityRestricted || speedRestricted ? "Restricted" : "Approved";
                reason = (authorityRestricted, speedRestricted) switch
                {
                    (true, true) => $"{authorityLimit} {speedLimit}",
                    (true, false) => authorityLimit,
                    (false, true) => speedLimit,
                    _ => ""
                };
            }
            blocks[original.BlockId] = original with
            {
                AuthorizedAuthorityMeters = authority, AuthorizedSpeedMetersPerSecond = speed,
                MovementStatus = status, MovementReason = reason
            };
        }
        var grants = blocks.Values.Where(block => block.Request is not null).ToDictionary(block => (_entries[Owner(block.BlockId)].Configuration.LineName, block.Request!.RequestId));
        foreach (var original in blocks.Values.ToArray())
        {
            int owner = Owner(original.BlockId);
            if (!_entries[owner].Configuration.CoordinatedRoutes || original.Continuation is not { } continuation) continue;
            var peers = Enumerable.Range(0, _entries.Length).Where(index => _entries[index].Configuration.LineName == _entries[owner].Configuration.LineName);
            bool prepared = peers.All(index => snapshots[index].RouteAssessments.Any(check => check.BlockId == original.BlockId
                && check.Continuation?.SuccessorRequestId == continuation.SuccessorRequestId
                && check.Continuation?.PredecessorRequestId == continuation.PredecessorRequestId));
            WaysideContinuation? approved = null;
            if (prepared && grants.TryGetValue((_entries[owner].Configuration.LineName, continuation.PredecessorRequestId), out var predecessor)
                && predecessor.AuthorizedAuthorityMeters > predecessor.LengthMeters + 1e-6 && predecessor.AuthorizedSpeedMetersPerSecond > 0)
            {
                approved = continuation with
                {
                    AuthorizedAuthorityMeters = Math.Min(continuation.AuthorizedAuthorityMeters, predecessor.AuthorizedAuthorityMeters - predecessor.LengthMeters),
                    AuthorizedSpeedMetersPerSecond = Math.Min(continuation.AuthorizedSpeedMetersPerSecond, predecessor.AuthorizedSpeedMetersPerSecond)
                };
            }
            blocks[original.BlockId] = original with { Continuation = approved };
        }
        return blocks.Values.ToArray();
    }
}
