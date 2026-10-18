using CTC.Core.Models;
using TrainControl.Contracts.Enums;

namespace CTC.Core.Dispatching;

/// <summary>
/// Fixed-block suggested authority: how far a train may travel along its route before
/// reaching the first block it must not enter.
/// </summary>
/// <remarks>
/// <para>
/// Calculated when a train is released (<see cref="CalculateInitialAuthorityMeters"/>) and
/// when the dispatcher re-plans a released train (<see cref="CalculateUpdatedAuthorityMeters"/>).
/// It is not recalculated automatically as the train moves: CTC issues a new suggestion only
/// at release and when the dispatcher changes the plan.
/// </para>
/// <para>
/// Deliberately out of scope: braking curves, moving-block/MBO logic, switch-conflict and
/// train-following prediction, and signal-based authority.
/// </para>
/// </remarks>
public static class AuthorityManager
{
    /// <summary>
    /// A block a train must not enter: it is occupied, the Track Controller reports it closed
    /// for maintenance, or CTC has requested it be closed. Unknown occupancy is not treated
    /// as occupied.
    /// </summary>
    /// <remarks>
    /// Both pending maintenance transitions are unsafe: a close CTC requested that the Track
    /// Controller has not yet confirmed (confirmed Open, requested Closed), and a reopen CTC
    /// requested while the block is still confirmed Closed (confirmed Closed, requested Open).
    /// Maintenance stops making a block unsafe only when both states are Open.
    /// </remarks>
    public static bool IsUnsafe(CtcBlockState block) => IsUnsafe(block, ignoreOccupancy: false);

    /// <summary>
    /// Authority for a train being released: walks <paramref name="route"/> (the train's
    /// scheduled blocks in travel order, starting with the block it is released at) and sums
    /// the lengths of the blocks up to, but not including, the first unsafe one. The train is
    /// assumed to be at the beginning of the first block, so that block's whole length counts.
    /// Returns 0 when the first block itself is unsafe (including occupied: the train has not
    /// entered it yet, so the occupant is something else), and the whole route's length when
    /// nothing ahead is unsafe.
    /// </summary>
    /// <example>
    /// 50 m blocks, B1 clear, B2 clear, B3 occupied: authority = 100 m.
    /// </example>
    public static double CalculateInitialAuthorityMeters(IEnumerable<CtcBlockState> route) =>
        CalculateAuthorityMeters(route, trainOccupiesStartBlock: false);

    /// <summary>
    /// Authority for a train that is already on the line, along <paramref name="route"/>
    /// starting with the block the train is in (its current block). Same rules as
    /// <see cref="CalculateInitialAuthorityMeters"/>, except that the start block being
    /// reported occupied does not stop the authority: CTC assumes the occupant is this train.
    /// </summary>
    /// <remarks>
    /// Conservative assumptions: the train is still treated as being at the beginning of its
    /// start block (its position within the block is unknown), and a start block closed or
    /// pending closure for maintenance still gives 0. Occupancy is evaluated only for the
    /// blocks ahead of the start block.
    /// </remarks>
    public static double CalculateUpdatedAuthorityMeters(IEnumerable<CtcBlockState> route) =>
        CalculateAuthorityMeters(route, trainOccupiesStartBlock: true);

    private static double CalculateAuthorityMeters(IEnumerable<CtcBlockState> route, bool trainOccupiesStartBlock)
    {
        ArgumentNullException.ThrowIfNull(route);

        return route
            .TakeWhile((block, index) => !IsUnsafe(block, ignoreOccupancy: trainOccupiesStartBlock && index == 0))
            .Sum(block => block.LengthMeters);
    }

    private static bool IsUnsafe(CtcBlockState block, bool ignoreOccupancy)
    {
        ArgumentNullException.ThrowIfNull(block);

        return (!ignoreOccupancy && block.Occupancy == OccupancyState.Occupied)
            || block.ConfirmedMaintenanceState == MaintenanceState.Closed
            || block.RequestedMaintenanceState == MaintenanceState.Closed;
    }
}
