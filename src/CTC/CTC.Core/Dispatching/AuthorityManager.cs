using CTC.Core.Models;
using TrainControl.Contracts.Enums;

namespace CTC.Core.Dispatching;

/// <summary>
/// Fixed-block suggested authority: how far a train may travel along ITS scheduled route
/// before reaching the first block it must not enter.
/// </summary>
/// <remarks>
/// <para>
/// Only the INITIAL authority at dispatch is calculated. CTC cannot recalculate it once the
/// train moves, because BlockStatusMessage occupancy does not say which train occupies a
/// block, so CTC cannot tell its own train from another one.
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
    public static bool IsUnsafe(CtcBlockState block)
    {
        ArgumentNullException.ThrowIfNull(block);

        return block.Occupancy == OccupancyState.Occupied
            || block.ConfirmedMaintenanceState == MaintenanceState.Closed
            || block.RequestedMaintenanceState == MaintenanceState.Closed;
    }

    /// <summary>
    /// Walks <paramref name="route"/> (the train's scheduled blocks in travel order, starting
    /// with the block it is released at) and sums the lengths of the blocks up to, but not
    /// including, the first unsafe one. The train is assumed to be at the beginning of the
    /// first block, so that block's whole length counts. Returns 0 when the first block itself
    /// is unsafe, and the whole route's length when nothing ahead is unsafe.
    /// </summary>
    /// <example>
    /// 50 m blocks, B1 clear, B2 clear, B3 occupied: authority = 100 m.
    /// </example>
    public static double CalculateInitialAuthorityMeters(IEnumerable<CtcBlockState> route)
    {
        ArgumentNullException.ThrowIfNull(route);

        return route
            .TakeWhile(block => !IsUnsafe(block))
            .Sum(block => block.LengthMeters);
    }
}
