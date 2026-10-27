using TrainControl.Contracts.Messages;
using System.Globalization;
using TrackController.Contracts.Messages;
using TrainControl.Contracts.Enums;

namespace TrackController.TestUI.Wpf.ViewModels;

/// <summary>Simulates CTC route updates from manually sent block occupancy without calculating controller outputs.</summary>
public sealed partial class MainWindowViewModel
{
    private bool _movementOriginObserved;
    private bool _nextBlockObservedClear;
    private string _movementProgress = "Send a movement request to start following its route.";

    public string MovementProgress
    {
        get => _movementProgress;
        private set => SetProperty(ref _movementProgress, value);
    }

    // A cleared origin retires its request in the controller. Keep refreshing feedback while
    // the user edits the next occupancy, but do not repeatedly send that retired request.
    private bool HasMovementOriginCleared => _movementOriginObserved && _appliedFeedback?.Any(block => block.BlockId == _appliedRequest?.TargetBlockId && block.Occupancy == OccupancyState.Clear) == true;

    private List<string> ParseRequestedPath() => RequestedPath.Split([',', ' ', '→', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private double? CalculateDraftAuthority()
    {
        var route = new[] { TargetBlock }.Concat(ParseRequestedPath()).ToArray();
        if (route.Distinct().Count() != route.Length)
        {
            return null;
        }

        double distance = 0;
        for (int i = 0; i < route.Length; i++)
        {
            var block = Blocks.SingleOrDefault(block => block.BlockId == route[i]);
            if (block is null || !double.IsFinite(block.LengthMeters) || block.LengthMeters <= 0)
            {
                return null;
            }

            if (i + 1 < route.Length)
            {
                if (!block.NextBlockIds.Contains(route[i + 1]))
                {
                    return null;
                }

                // Both endpoints are block entries: include the current block, but not
                // the destination block. Arrival can then consume the final allowance.
                distance += block.LengthMeters;
            }
        }

        return double.IsFinite(distance) ? distance : null;
    }

    private async Task FollowMovementAsync(List<TrackModelBlockStateMessage> feedback)
    {
        if (_appliedRequest is not { } request)
        {
            return;
        }

        var origin = feedback.SingleOrDefault(block => block.BlockId == request.TargetBlockId);
        var next = feedback.SingleOrDefault(block => block.BlockId == request.PathBlockIds.FirstOrDefault());
        _movementOriginObserved |= origin?.Occupancy == OccupancyState.Occupied;
        _nextBlockObservedClear |= next?.Occupancy == OccupancyState.Clear;
        if (!_movementOriginObserved || origin?.Occupancy != OccupancyState.Clear)
        {
            return;
        }

        if (request.PathBlockIds.Count == 0 || request.SuggestedAuthorityMeters <= 0)
        {
            await CancelMovementAsync();
            TargetBlock = request.TargetBlockId;
            RequestedPath = "";
            MovementProgress = $"Final block {request.TargetBlockId} cleared. The movement request has ended; send a new route to continue.";
            Status = MovementProgress;
            return;
        }

        // This is a single test train following its next block, not train identification.
        // Never absorb an existing occupied obstacle or jump over an unobserved block.
        if (next?.Occupancy != OccupancyState.Occupied || !_nextBlockObservedClear)
        {
            MovementProgress = !_nextBlockObservedClear && next?.Occupancy == OccupancyState.Occupied
                ? $"Block {next.BlockId} was not observed clear on this request. Automatic movement is waiting; check the occupancy or send a new request."
                : $"Block {request.TargetBlockId} cleared. Waiting for next route block {request.PathBlockIds[0]} to become occupied; no block is skipped.";
            return;
        }

        var definition = Blocks.SingleOrDefault(block => block.BlockId == request.TargetBlockId);
        if (definition is null || !double.IsFinite(definition.LengthMeters) || definition.LengthMeters <= 0)
        {
            MovementProgress = "Cannot advance the request without the previous block's length from the controller.";
            return;
        }

        var updated = new MovementSuggestionMessage
        {
            RequestId = Guid.NewGuid().ToString("N"),
            TrainId = request.TrainId,
            TargetBlockId = next.BlockId,
            EntryBlockId = request.TargetBlockId,
            PathBlockIds = request.PathBlockIds.Skip(1).ToList(),
            SuggestedSpeedMetersPerSecond = request.SuggestedSpeedMetersPerSecond,
            // Shift the reference boundary while preserving the original requested endpoint.
            SuggestedAuthorityMeters = Math.Max(0, request.SuggestedAuthorityMeters - definition.LengthMeters)
        };

        await CancelMovementAsync();
        TargetBlock = updated.TargetBlockId;
        EntryBlock = updated.EntryBlockId;
        RequestedPath = string.Join(", ", updated.PathBlockIds);
        SuggestedSpeed = (updated.SuggestedSpeedMetersPerSecond / DisplayUnits.MetersPerSecondPerMph).ToString("0.######", CultureInfo.CurrentCulture);
        TrainLabel = updated.TrainId;
        if (updated.PathBlockIds.Count == 0 || updated.SuggestedAuthorityMeters <= 0)
        {
            RequestedPath = "";
            MovementProgress = $"Arrived at final block {updated.TargetBlockId}. Requested authority is 0; movement request cleared. Send a new route to continue.";
            Status = MovementProgress;
            return;
        }

        MovementProgress = $"Block {request.TargetBlockId} → {updated.TargetBlockId}: sending the updated movement request.";
        await SendAsync(updated);
        SetAppliedRequest(updated);
        MovementProgress = $"Advanced {request.TargetBlockId} → {updated.TargetBlockId}. Requested authority reduced by {DisplayUnits.FormatFeet(definition.LengthMeters)} ft. Authorized values come from the controller.";
    }
}
