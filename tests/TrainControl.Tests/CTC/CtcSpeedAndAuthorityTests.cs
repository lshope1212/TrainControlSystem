using CTC.Core.Dispatching;
using CTC.Core.Models;
using TrainControl.Contracts.Enums;

namespace TrainControl.Tests.CTC;

[TestClass]
public class CtcSpeedAndAuthorityTests
{
    private static readonly TimeSpan Noon = new TimeSpan(12, 0, 0);

    private static CtcBlockState Block(string blockId, double lengthMeters = 50, double speedLimitKph = 50) => new CtcBlockState
    {
        BlockId = blockId,
        LengthMeters = lengthMeters,
        SpeedLimitKilometersPerHour = speedLimitKph,
    };

    private static List<CtcBlockState> Route(int count) => Enumerable.Range(1, count).Select(n => Block($"B{n}")).ToList();

    private static List<CtcBlockState> BlueRoute(CtcSystemState state, ScheduledTrain train) =>
        train.Route.Select(block => state.FindBlock(block.BlockId)!).ToList();

    // ---- Permitted speed ----

    [TestMethod]
    public void MaxPermittedSpeed_BlueLine_IsBlockLimit()
    {
        double max = SpeedPlanner.GetMaxPermittedSpeedMetersPerSecond(Block("A1"), Block("A2"));

        Assert.AreEqual(50.0 / 3.6, max, 1e-9);
    }

    [TestMethod]
    public void MaxPermittedSpeed_FastTrack_IsVehicleMaximum()
    {
        double max = SpeedPlanner.GetMaxPermittedSpeedMetersPerSecond(Block("A1", speedLimitKph: 100), Block("A2", speedLimitKph: 100));

        Assert.AreEqual(70.0, TrainPerformance.MaxSpeedKilometersPerHour);
        Assert.AreEqual(70.0 / 3.6, max, 1e-9);
    }

    [TestMethod]
    public void MaxPermittedSpeed_UsesLowerOfCurrentAndNextBlockLimits()
    {
        double max = SpeedPlanner.GetMaxPermittedSpeedMetersPerSecond(Block("A1", speedLimitKph: 60), Block("A2", speedLimitKph: 40));

        Assert.AreEqual(40.0 / 3.6, max, 1e-9);
    }

    // ---- Schedule feasibility (QueueSchedule) ----

    [TestMethod]
    public void QueueSchedule_FourSecondsPerBlueBlock_IsAccepted()
    {
        // 50 m in 4 s = 12.5 m/s = 45 km/h, under the 50 km/h limit.
        var service = BlueLine.CreateService();

        service.QueueSchedule([BlueLine.Train("000", Noon, secondsPerBlock: 4)]);

        Assert.HasCount(1, service.State.DispatchQueue);
    }

    [TestMethod]
    public void QueueSchedule_ThreeSecondsPerBlueBlock_IsRejected()
    {
        // 50 m in 3 s = 60 km/h > 50 km/h.
        var service = BlueLine.CreateService();
        var changes = new List<CtcStateChangeKind>();
        service.StateChanged += (_, e) => changes.Add(e.Kind);

        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => service.QueueSchedule([BlueLine.Train("000", Noon, secondsPerBlock: 3)]));

        Assert.AreEqual(
            "Train 000 cannot travel from A1 to A2 in the scheduled time. Required: 60.0 km/h. Maximum allowed: 50.0 km/h.",
            ex.Message);
        Assert.IsEmpty(service.State.ScheduledTrains);
        Assert.IsEmpty(service.State.DispatchQueue);
        Assert.IsEmpty(changes);
    }

    [TestMethod]
    public void QueueSchedule_OneTooFastSegment_NamesThatSegment()
    {
        var service = BlueLine.CreateService();
        var train = BlueLine.Train("001", Noon, secondsPerBlock: 4);
        train.Route[3].ArrivalTime = train.Route[2].ArrivalTime + TimeSpan.FromSeconds(3);

        var ex = Assert.ThrowsExactly<ArgumentException>(() => service.QueueSchedule([train]));

        Assert.StartsWith("Train 001 cannot travel from A3 to A4 in the scheduled time.", ex.Message);
    }

    [TestMethod]
    public void QueueSchedule_ExceedingVehicleMaximumOnFastTrack_IsRejected()
    {
        // 100 km/h track: 50 m in 2 s = 90 km/h is legal for the track but above the 70 km/h vehicle.
        var service = BlueLine.CreateService(speedLimitKph: 100);

        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => service.QueueSchedule([BlueLine.Train("000", Noon, secondsPerBlock: 2)]));

        Assert.AreEqual(
            "Train 000 cannot travel from A1 to A2 in the scheduled time. Required: 90.0 km/h. Maximum allowed: 70.0 km/h.",
            ex.Message);
    }

    [TestMethod]
    public void QueueSchedule_WithinVehicleMaximumOnFastTrack_IsAccepted()
    {
        // 50 m in 3 s = 60 km/h: over the Blue limit but fine on 100 km/h track with a 70 km/h vehicle.
        var service = BlueLine.CreateService(speedLimitKph: 100);

        service.QueueSchedule([BlueLine.Train("000", Noon, secondsPerBlock: 3)]);

        Assert.HasCount(1, service.State.DispatchQueue);
    }

    [TestMethod]
    public void QueueSchedule_NonIncreasingTimes_IsRejected()
    {
        var service = BlueLine.CreateService();
        var train = BlueLine.Train("000", Noon);
        train.Route[1].ArrivalTime = Noon;

        var ex = Assert.ThrowsExactly<ArgumentException>(() => service.QueueSchedule([train]));

        Assert.AreEqual("Train 000 must enter A2 later than it enters A1.", ex.Message);
    }

    [TestMethod]
    public void QueueSchedule_SparseFeasibleSpan_IsAccepted()
    {
        // A1 -> B10 timed only at the ends: 9 blocks x 50 m = 450 m in 36 s = 12.5 m/s = 45 km/h.
        var service = BlueLine.CreateService();

        service.QueueSchedule([BlueLine.Train("000", Noon, secondsPerBlock: 4, timedBlocks: ["A1", "B10"])]);

        Assert.HasCount(1, service.State.DispatchQueue);
    }

    [TestMethod]
    public void QueueSchedule_SparseSpanTooFast_IsRejectedForWholeSpan()
    {
        // 450 m in 27 s = 60 km/h > 50 km/h; the message names the timed blocks, not a routed-through one.
        var service = BlueLine.CreateService();

        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => service.QueueSchedule([BlueLine.Train("000", Noon, secondsPerBlock: 3, timedBlocks: ["A1", "B10"])]));

        Assert.AreEqual(
            "Train 000 cannot travel from A1 to B10 in the scheduled time. Required: 60.0 km/h. Maximum allowed: 50.0 km/h.",
            ex.Message);
    }

    [TestMethod]
    public void MaxPermittedSpeed_OverSpan_IsLowestLimitInSpan()
    {
        double max = SpeedPlanner.GetMaxPermittedSpeedMetersPerSecond(
            Block("A1", speedLimitKph: 60), Block("A2", speedLimitKph: 30), Block("A3", speedLimitKph: 60));

        Assert.AreEqual(30.0 / 3.6, max, 1e-9);
    }

    [TestMethod]
    public void QueueSchedule_BlockWithoutSpeedLimit_IsRejected()
    {
        var service = BlueLine.CreateService(speedLimitKph: 0);

        var ex = Assert.ThrowsExactly<ArgumentException>(() => service.QueueSchedule([BlueLine.Train("000", Noon)]));

        Assert.Contains("the track layout gives it no speed limit", ex.Message);
    }

    // ---- Initial suggested speed ----

    [TestMethod]
    public void InitialSpeed_FourSecondBlueSegmentAtDeparture_Is12Point5MetersPerSecond()
    {
        var service = BlueLine.CreateService();
        var train = BlueLine.Train("000", Noon, secondsPerBlock: 4);

        var speed = SpeedPlanner.CalculateInitialSpeed(train, BlueRoute(service.State, train), Noon);

        Assert.AreEqual(12.5, speed.SpeedMetersPerSecond, 1e-9);
        Assert.AreEqual(45.0, speed.SpeedMetersPerSecond * 3.6, 1e-9);
        Assert.IsFalse(speed.IsLate);
    }

    [TestMethod]
    public void InitialSpeed_TargetsNextTimedBlock()
    {
        // Next timed block is A5: 4 blocks x 50 m = 200 m in 20 s = 10 m/s.
        var service = BlueLine.CreateService();
        var train = BlueLine.Train("000", Noon, secondsPerBlock: 5, timedBlocks: ["A1", "A5", "B10"]);

        var speed = SpeedPlanner.CalculateInitialSpeed(train, BlueRoute(service.State, train), Noon);

        Assert.AreEqual(10.0, speed.SpeedMetersPerSecond, 1e-9);
        Assert.AreEqual("A5", speed.TargetBlockId);
        Assert.IsFalse(speed.IsLate);
    }

    [TestMethod]
    public void InitialSpeed_ReleasedAfterDeparture_UsesRemainingTime()
    {
        // Next block due 10 s after departure; released 2 s late -> 50 m in 8 s.
        var service = BlueLine.CreateService();
        var train = BlueLine.Train("000", Noon, secondsPerBlock: 10);

        var speed = SpeedPlanner.CalculateInitialSpeed(train, BlueRoute(service.State, train), Noon + TimeSpan.FromSeconds(2));

        Assert.AreEqual(6.25, speed.SpeedMetersPerSecond, 1e-9);
        Assert.IsFalse(speed.IsLate);
    }

    [TestMethod]
    public void InitialSpeed_RequiredAboveLimit_IsCappedAtPermittedMaximum()
    {
        // 4 s schedule, released 1 s late -> 50 m in 3 s would need 60 km/h.
        var service = BlueLine.CreateService();
        var train = BlueLine.Train("000", Noon, secondsPerBlock: 4);

        var speed = SpeedPlanner.CalculateInitialSpeed(train, BlueRoute(service.State, train), Noon + TimeSpan.FromSeconds(1));

        Assert.AreEqual(50.0 / 3.6, speed.SpeedMetersPerSecond, 1e-9);
        Assert.IsFalse(speed.IsLate);
    }

    [TestMethod]
    public void InitialSpeed_PastNextBlockTime_IsPermittedMaximumAndLate()
    {
        var service = BlueLine.CreateService();
        var train = BlueLine.Train("000", Noon, secondsPerBlock: 4);

        var speed = SpeedPlanner.CalculateInitialSpeed(train, BlueRoute(service.State, train), Noon + TimeSpan.FromSeconds(4));

        Assert.AreEqual(50.0 / 3.6, speed.SpeedMetersPerSecond, 1e-9);
        Assert.IsTrue(speed.IsLate);
    }

    // ---- Initial suggested authority ----

    [TestMethod]
    public void Authority_NothingAhead_IsWholeRoute()
    {
        Assert.AreEqual(250.0, AuthorityManager.CalculateInitialAuthorityMeters(Route(5)));
    }

    [TestMethod]
    public void Authority_OccupiedBlock_StopsAtPrecedingBoundary()
    {
        // B1 clear, B2 clear, B3 occupied -> 100 m.
        var route = Route(5);
        route[2].Occupancy = OccupancyState.Occupied;

        Assert.AreEqual(100.0, AuthorityManager.CalculateInitialAuthorityMeters(route));
    }

    [TestMethod]
    public void Authority_MaintenanceClosedBlock_StopsAtPrecedingBoundary()
    {
        var route = Route(5);
        route[3].RequestedMaintenanceState = MaintenanceState.Closed;

        Assert.AreEqual(150.0, AuthorityManager.CalculateInitialAuthorityMeters(route));
    }

    [TestMethod]
    public void Authority_UnsafeStartBlock_IsZero()
    {
        var route = Route(5);
        route[0].Occupancy = OccupancyState.Occupied;

        Assert.AreEqual(0.0, AuthorityManager.CalculateInitialAuthorityMeters(route));
    }

    [TestMethod]
    public void Authority_UnknownOccupancy_IsNotTreatedAsOccupied()
    {
        var route = Route(3);
        route[1].Occupancy = OccupancyState.Unknown;

        Assert.AreEqual(150.0, AuthorityManager.CalculateInitialAuthorityMeters(route));
    }

    // ---- Maintenance safety (confirmed vs requested) ----

    [TestMethod]
    [DataRow(MaintenanceState.Closed, MaintenanceState.Closed, DisplayName = "Confirmed closed")]
    [DataRow(MaintenanceState.Open, MaintenanceState.Closed, DisplayName = "Close requested, not confirmed")]
    [DataRow(MaintenanceState.Closed, MaintenanceState.Open, DisplayName = "Reopen requested, still confirmed closed")]
    public void IsUnsafe_ClosedOrPendingMaintenance_IsUnsafe(MaintenanceState confirmed, MaintenanceState requested)
    {
        var block = Block("B1");
        block.Occupancy = OccupancyState.Clear;
        block.ConfirmedMaintenanceState = confirmed;
        block.RequestedMaintenanceState = requested;

        Assert.IsTrue(AuthorityManager.IsUnsafe(block));
    }

    [TestMethod]
    public void IsUnsafe_ConfirmedAndRequestedOpen_IsSafeUnlessOccupied()
    {
        var block = Block("B1");
        block.Occupancy = OccupancyState.Clear;
        block.ConfirmedMaintenanceState = MaintenanceState.Open;
        block.RequestedMaintenanceState = MaintenanceState.Open;

        Assert.IsFalse(AuthorityManager.IsUnsafe(block));

        block.Occupancy = OccupancyState.Occupied;

        Assert.IsTrue(AuthorityManager.IsUnsafe(block));
    }

    [TestMethod]
    public void Authority_ConfirmedClosedBlock_StopsAtPrecedingBoundary()
    {
        var route = Route(5);
        route[3].ConfirmedMaintenanceState = MaintenanceState.Closed;

        Assert.AreEqual(150.0, AuthorityManager.CalculateInitialAuthorityMeters(route));
    }
}
