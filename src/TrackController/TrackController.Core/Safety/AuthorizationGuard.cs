using TrackController.Contracts.Messages;
using TrainControl.Contracts.Enums;

namespace TrackController.Core.Safety;

/// <summary>A separate, differently structured check of every positive movement output.</summary>
public static class AuthorizationGuard
{
    /// <summary>Independently checks a proposed authorization against topology and measured conditions.</summary>
    public static bool IsSafe(WaysideAuthorization output, IReadOnlyList<WaysideBlockStatus> blocks, WaysideConfigurationMessage configuration)
    {
        if (output.AuthorityReference != WaysideMovementRequestMessage.BlockEntryReference || !double.IsFinite(output.AuthorizedAuthorityMeters) || !double.IsFinite(output.AuthorizedSpeedMetersPerSecond) || output.AuthorizedAuthorityMeters < 0 || output.AuthorizedSpeedMetersPerSecond < 0)
        {
            return false;
        }

        if (output.AuthorizedAuthorityMeters == 0)
        {
            return output.AuthorizedSpeedMetersPerSecond == 0;
        }

        if (output.AuthorizedSpeedMetersPerSecond <= 0)
        {
            return false;
        }

        if (output.AuthorizedAuthorityMeters > output.SuggestedAuthorityMeters || output.AuthorizedSpeedMetersPerSecond > output.SuggestedSpeedMetersPerSecond)
        {
            return false;
        }

        var sequence = new[]
                {
            output.TargetBlockId
        }.Concat(output.PathBlockIds).ToArray();
        var owned = TrackController.Core.Models.WaysideConfiguration.GetOwnedIds(configuration);
        if (sequence.Any(id => !owned.Contains(id)))
        {
            return false;
        }

        double remaining = output.AuthorizedAuthorityMeters;
        for (int i = 0; i < sequence.Length && (i == 0 || remaining > 0.000001); i++)
        {
            var block = blocks.SingleOrDefault(blockStatus => blockStatus.BlockId == sequence[i]);
            if (block is null || !block.IsFresh || block.IsFaulted || block.Occupancy == OccupancyState.Unknown || (i == 0 ? block.Occupancy != OccupancyState.Occupied : block.Occupancy != OccupancyState.Clear) || block.Maintenance == "Closed" || (i > 0 && block.Maintenance != "Open") || output.AuthorizedSpeedMetersPerSecond > block.SpeedLimitMetersPerSecond)
            {
                return false;
            }

            if (block.HasCrossing && (block.CommandedCrossing != CrossingState.Closed || block.ReportedCrossing != CrossingState.Closed))
            {
                return false;
            }

            if (block.HasSignal && (block.CommandedSignal is not (SignalState.Green or SignalState.SuperGreen or SignalState.Yellow) || block.ReportedSignal is not (SignalState.Green or SignalState.SuperGreen or SignalState.Yellow)))
            {
                return false;
            }

            if ((block.CommandedSignal == SignalState.Yellow || block.ReportedSignal == SignalState.Yellow) && block.HasSignal && output.AuthorizedSpeedMetersPerSecond > 5)
            {
                return false;
            }

            remaining -= block.LengthMeters;
            if (remaining > 0.000001 && (i + 1 >= sequence.Length || !block.NextBlockIds.Contains(sequence[i + 1])))
            {
                return false;
            }

            if (block.HasSwitch)
            {
                if (block.CommandedSwitch == SwitchPosition.Unknown || block.CommandedSwitch != block.ReportedSwitch)
                {
                    return false;
                }

                var definition = configuration.Blocks.Single(blockDefinition => blockDefinition.BlockId == block.BlockId);
                var exit = block.ReportedSwitch == SwitchPosition.Normal ? definition.NormalNextBlockId : definition.ReverseNextBlockId;
                if (remaining > 0.000001)
                {
                    string? entry = i > 0 ? sequence[i - 1] : output.EntryBlockId;
                    if (definition.StemBlockId is { } stem)
                    {
                        if (!((entry == stem && sequence[i + 1] == exit) || (entry == exit && sequence[i + 1] == stem)))
                        {
                            return false;
                        }
                    }
                    else if (sequence[i + 1] != exit)
                    {
                        return false;
                    }
                }
            }
        }

        return remaining <= 0.000001;
    }
}
