using TrainControl.Contracts.Messages;
using MovementResult = (TrainControl.Contracts.Messages.MovementSuggestionMessage Request, double AuthorizedSpeedMetersPerSecond, double AuthorizedAuthorityMeters, string Status, string Reason);

namespace TrackController.Core.Services;

public sealed partial class TrackControllerService
{
    /// <summary>The issued limit of the current CTC movement, retained through unchanged renewals.</summary>
    private sealed class BlockAuthority(MovementSuggestionMessage request, double meters)
    {
        public MovementSuggestionMessage Request { get; } = request;
        public double Meters { get; } = meters;
    }

    /// <summary>Track-condition changes cannot shorten or extend an unchanged movement's nonzero authority.</summary>
    private MovementResult HoldBlockAuthority(MovementResult result)
    {
        var held = GetBlock(result.Request.TargetBlockId).Authority;
        if (held is null || result.AuthorizedAuthorityMeters <= 0)
        {
            return result;
        }

        var previous = (held.Request, 0d, held.Meters, "", "");
        var proposed = result with { AuthorizedAuthorityMeters = held.Meters };
        if (held.Request.EntryBlockId != result.Request.EntryBlockId
            || held.Request.TrainId != result.Request.TrainId
            || !GetCoveredBlocks(previous).SequenceEqual(GetCoveredBlocks(proposed)))
        {
            return Stop(result, "The issued route no longer matches this request. CTC must submit a replacement movement.");
        }

        if (result.AuthorizedAuthorityMeters + HandoffTolerance < held.Meters)
        {
            return Stop(result, $"The issued {held.Meters:0.###} m authority is no longer available. Authority is zero instead of a shortened nonzero value within this block.");
        }

        // Track conditions may improve or CTC may request more, but the train must keep counting down
        // from the same block-entry value. All speed and safety restrictions still apply.
        return result with
        {
            AuthorizedAuthorityMeters = held.Meters,
            Status = held.Meters < result.Request.SuggestedAuthorityMeters ? "Restricted" : result.Status,
            Reason = held.Meters < result.AuthorizedAuthorityMeters
                ? "Authority held at its issued block-entry value until the next block or a new CTC movement."
                : result.Reason
        };
    }

    /// <summary>Records only the final, independently checked output, never an intermediate PLC candidate.</summary>
    private void RememberBlockAuthority(MovementResult result)
    {
        var block = GetBlock(result.Request.TargetBlockId);
        if (result.AuthorizedAuthorityMeters > 0)
        {
            block.Authority ??= new BlockAuthority(result.Request, result.AuthorizedAuthorityMeters);
        }
    }

    /// <summary>
    /// CTC owns departure and replanning. Its replacement request (or changed authority limit) starts
    /// a new evaluation without requiring speed telemetry that Track Model does not publish.
    /// Identical renewals and positive speed-only updates retain the movement's issued limit.
    /// </summary>
    private void PrepareCtcMovement(MovementSuggestionMessage request)
    {
        var block = GetBlock(request.TargetBlockId);
        if (!_requests.TryGetValue(request.TargetBlockId, out var current)
            || current.Value.RequestId != request.RequestId
            || current.Value.TrainId != request.TrainId
            || current.Value.SuggestedAuthorityMeters != request.SuggestedAuthorityMeters
            || (current.Value.SuggestedSpeedMetersPerSecond <= 0 && request.SuggestedSpeedMetersPerSecond > 0))
        {
            block.Authority = null;
        }
    }
}
