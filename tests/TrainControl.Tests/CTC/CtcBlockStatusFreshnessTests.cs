using CTC.Core.Dispatching;
using CTC.Core.Models;
using CTC.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.CTC;

/// <summary>
/// Live wayside status: applied in order, late reports never overwrite newer ones, repeats are
/// silent, and a status whose reports stop goes stale (Unknown, unsafe) on wall time.
/// </summary>
[TestClass]
public class CtcBlockStatusFreshnessTests
{
    private static (CTCService Service, ManualClock Clock, List<CtcStateChangedEventArgs> Changes) Create()
    {
        var clock = new ManualClock();
        var service = new CTCService(new FakeMessageSender(), clock);
        service.ApplyTrackLayout(BlueLine.CreateLayout());
        var changes = new List<CtcStateChangedEventArgs>();
        service.StateChanged += (_, e) => changes.Add(e);
        return (service, clock, changes);
    }

    private static BlockStatusMessage Live(string blockId, OccupancyState occupancy, DateTimeOffset reportedAt, SwitchPosition position = SwitchPosition.Unknown) => new BlockStatusMessage
    {
        BlockId = blockId,
        Occupancy = occupancy,
        Switch = position,
        ReportedAtUtc = reportedAt,
    };

    [TestMethod]
    public void LateReport_OlderThanTheAppliedOne_IsIgnored()
    {
        var (service, clock, _) = Create();
        var now = clock.GetUtcNow();

        service.ApplyBlockStatus(Live("A2", OccupancyState.Occupied, now));
        service.ApplyBlockStatus(Live("A2", OccupancyState.Clear, now - TimeSpan.FromMilliseconds(500)));

        Assert.AreEqual(OccupancyState.Occupied, service.State.FindBlock("A2")!.Occupancy);
    }

    [TestMethod]
    public void NewerReport_IsApplied()
    {
        var (service, clock, _) = Create();
        var now = clock.GetUtcNow();

        service.ApplyBlockStatus(Live("A2", OccupancyState.Occupied, now));
        service.ApplyBlockStatus(Live("A2", OccupancyState.Clear, now + TimeSpan.FromMilliseconds(500)));

        Assert.AreEqual(OccupancyState.Clear, service.State.FindBlock("A2")!.Occupancy);
    }

    [TestMethod]
    public void RepeatedIdenticalReport_RaisesNoChange()
    {
        var (service, clock, changes) = Create();
        var now = clock.GetUtcNow();

        service.ApplyBlockStatus(Live("A5", OccupancyState.Clear, now, SwitchPosition.Normal));
        service.ApplyBlockStatus(Live("A5", OccupancyState.Clear, now + TimeSpan.FromMilliseconds(500), SwitchPosition.Normal));

        Assert.HasCount(1, changes.Where(change => change.Kind == CtcStateChangeKind.BlockStatus));
    }

    [TestMethod]
    public async Task ReportedEquipment_IsWhatCtcShows_RequestsNeverChangeIt()
    {
        var (service, clock, _) = Create();
        service.State.FindBlock("A5")!.HasSwitch = true;
        service.ApplyBlockStatus(Live("A5", OccupancyState.Clear, clock.GetUtcNow(), SwitchPosition.Normal));

        // A switch request is not a position: the displayed switch stays Normal until reported.
        await service.SetSwitchPositionAsync("A5", SwitchPosition.Reverse);
        Assert.AreEqual(SwitchPosition.Normal, service.State.FindBlock("A5")!.Switch);

        service.ApplyBlockStatus(Live("A5", OccupancyState.Clear, clock.GetUtcNow() + TimeSpan.FromSeconds(1), SwitchPosition.Reverse));
        Assert.AreEqual(SwitchPosition.Reverse, service.State.FindBlock("A5")!.Switch);
    }

    [TestMethod]
    public void LiveStatusNotRefreshed_GoesStaleAndUnsafe()
    {
        var (service, clock, changes) = Create();
        service.ApplyBlockStatus(Live("A3", OccupancyState.Clear, clock.GetUtcNow(), SwitchPosition.Normal));

        clock.Advance(CTCService.BlockStatusStaleAfter);
        Assert.AreEqual(0, service.RefreshBlockStatusFreshness(), "Exactly at the limit it is still fresh.");

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(1, service.RefreshBlockStatusFreshness());

        var block = service.State.FindBlock("A3")!;
        Assert.IsTrue(block.IsStatusStale);
        Assert.AreEqual(OccupancyState.Unknown, block.Occupancy);
        Assert.AreEqual(SwitchPosition.Unknown, block.Switch);
        Assert.IsTrue(AuthorityManager.IsUnsafe(block));
        Assert.IsTrue(changes.Any(change => change.Kind == CtcStateChangeKind.BlockStatusStale && change.Message!.Contains("A3")));
        Assert.AreEqual(0, service.RefreshBlockStatusFreshness(), "Going stale is reported once.");
    }

    [TestMethod]
    public void FreshReportAfterStale_RestoresTheBlock()
    {
        var (service, clock, _) = Create();
        service.ApplyBlockStatus(Live("A3", OccupancyState.Clear, clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromSeconds(10));
        service.RefreshBlockStatusFreshness();

        service.ApplyBlockStatus(Live("A3", OccupancyState.Clear, clock.GetUtcNow()));

        var block = service.State.FindBlock("A3")!;
        Assert.IsFalse(block.IsStatusStale);
        Assert.AreEqual(OccupancyState.Clear, block.Occupancy);
    }

    [TestMethod]
    public void NonPeriodicStatus_FromTheTestUi_NeverGoesStale()
    {
        // The CTC TestUI sends a status once and expects it to stay until replaced.
        var (service, clock, _) = Create();
        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "A3", Occupancy = OccupancyState.Clear });

        clock.Advance(TimeSpan.FromHours(1));

        Assert.AreEqual(0, service.RefreshBlockStatusFreshness());
        Assert.AreEqual(OccupancyState.Clear, service.State.FindBlock("A3")!.Occupancy);
    }

    [TestMethod]
    public void LateReport_CannotMoveATrainBackward()
    {
        var (service, clock, _) = Create();
        var t0 = clock.GetUtcNow();
        service.ApplyBlockStatus(Live("A2", OccupancyState.Clear, t0));
        service.ApplyBlockStatus(Live("A2", OccupancyState.Occupied, t0 + TimeSpan.FromSeconds(2)));

        // An old "A2 clear" arriving late changes nothing, so a later real occupancy is still a single transition.
        service.ApplyBlockStatus(Live("A2", OccupancyState.Clear, t0 + TimeSpan.FromSeconds(1)));

        Assert.AreEqual(OccupancyState.Occupied, service.State.FindBlock("A2")!.Occupancy);
    }
}
