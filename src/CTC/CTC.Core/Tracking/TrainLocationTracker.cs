using CTC.Core.Models;
using TrainControl.Contracts.Enums;

namespace CTC.Core.Tracking;

/// <summary>
/// Outcome of one occupancy report: the train CTC moved forward, or the trains that could each
/// explain the occupancy (more than one means it is ambiguous and nobody moved).
/// </summary>
public sealed record TrainLocationResult(DispatchedTrainState? AdvancedTrain, IReadOnlyList<DispatchedTrainState> AmbiguousTrains)
{
    public static TrainLocationResult None { get; } = new TrainLocationResult(null, []);

    public bool IsAmbiguous => AmbiguousTrains.Count > 0;
}

/// <summary>
/// Sequential-occupancy train tracking: infers which dispatched train a newly occupied block
/// belongs to, the way a real CTC follows trains through anonymous track circuits.
/// </summary>
/// <remarks>
/// <para>
/// The Track Controller reports only that SOMETHING occupies a block. CTC knows where each train
/// entered (it dispatched it there) and which blocks it is expected to follow
/// (<see cref="DispatchedTrainState.ActiveRouteBlockIds"/>), so when the NEXT block of exactly one
/// train's active route goes from not-occupied to occupied, that train has moved into it.
/// </para>
/// <para>
/// Rules, all deliberately conservative (CTC never guesses):
/// only a transition INTO Occupied counts, so repeated snapshots never move a train twice;
/// a block clearing never moves or removes a train, so the trailing block may clear before or
/// after the next one is occupied; route blocks are never skipped; a train is advanced only once
/// its current block has been reported occupied; and if the occupancy could belong to more than
/// one train, no train is moved. An occupancy no train explains is simply left anonymous.
/// </para>
/// <para>
/// Arrival times play no part: trains run early and late, and only the route sequence and track
/// occupancy identify them.
/// </para>
/// </remarks>
public static class TrainLocationTracker
{
    /// <summary>
    /// Updates the dispatched trains in <paramref name="state"/> after <paramref name="block"/>'s
    /// occupancy changed from <paramref name="previousOccupancy"/> to its current value.
    /// </summary>
    public static TrainLocationResult ApplyOccupancy(CtcSystemState state, CtcBlockState block, OccupancyState previousOccupancy)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(block);

        // Only a block BECOMING occupied carries information; clears, Unknown and repeated
        // Occupied snapshots never move a train.
        if (block.Occupancy != OccupancyState.Occupied || previousOccupancy == OccupancyState.Occupied)
        {
            return TrainLocationResult.None;
        }

        var line = state.Lines.FirstOrDefault(line => line.Blocks.Contains(block));
        if (line is null)
        {
            return TrainLocationResult.None;
        }

        var trains = state.DispatchedTrains.Where(train => train.LineId == line.LineId && HasValidActiveRoute(train)).ToList();

        // Every train that could explain this occupancy: one arriving in its current block for the
        // first time (just released there), or one moving into its next route block.
        var arriving = trains.Where(train => train.CurrentBlockId == block.BlockId && !train.CurrentBlockOccupancyObserved).ToList();
        var advancing = trains.Where(train => NextBlockId(train) == block.BlockId).ToList();
        var candidates = arriving.Concat(advancing).ToList();

        if (candidates.Count > 1)
        {
            return new TrainLocationResult(null, candidates);
        }

        if (arriving.Count == 1)
        {
            // The train is now seen at the block CTC already places it in; the chain is established.
            arriving[0].CurrentBlockOccupancyObserved = true;
            return TrainLocationResult.None;
        }

        if (advancing.Count == 1 && advancing[0].CurrentBlockOccupancyObserved)
        {
            var train = advancing[0];
            train.CurrentBlockId = block.BlockId;
            train.ActiveRouteIndex++;
            train.CurrentBlockOccupancyObserved = true;
            return new TrainLocationResult(train, []);
        }

        return TrainLocationResult.None;
    }

    /// <summary>The block after the train's current block on its active route, or null at the end of it.</summary>
    public static string? NextBlockId(DispatchedTrainState train)
    {
        ArgumentNullException.ThrowIfNull(train);

        int next = train.ActiveRouteIndex + 1;
        return HasValidActiveRoute(train) && next < train.ActiveRouteBlockIds.Count ? train.ActiveRouteBlockIds[next] : null;
    }

    private static bool HasValidActiveRoute(DispatchedTrainState train) =>
        !string.IsNullOrEmpty(train.CurrentBlockId)
        && train.ActiveRouteIndex >= 0
        && train.ActiveRouteIndex < train.ActiveRouteBlockIds.Count
        && train.ActiveRouteBlockIds[train.ActiveRouteIndex] == train.CurrentBlockId;
}
