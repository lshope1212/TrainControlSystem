using CTC.Core.Models;
using CTC.Core.Services;
using TrackModel.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.CTC;

/// <summary>
/// The production layout reaching CTC: block identity is the Track Model's, used verbatim; a
/// repeated layout is a no-op; incompatible revisions are handled per line and safely.
/// </summary>
[TestClass]
public class CtcTrackLayoutSyncTests
{
    private static readonly TimeSpan Noon = new TimeSpan(12, 0, 0);

    /// <summary>The layout the real Track Model publishes for its bundled course Blue Line.</summary>
    private static TrackLayoutMessage ProductionBlueLayout()
    {
        var track = new TrackService();
        track.LoadLayout(BlueLineTrackLayout.Create());
        return track.CreateLayoutMessage();
    }

    private static TrackLineDefinition GreenLine() => new TrackLineDefinition
    {
        LineId = "GREEN",
        Name = "Green Line",
        Blocks =
        {
            new TrackBlockDefinition { BlockId = "GREEN-1", BlockNumber = 1, Section = "A", LengthMeters = 100, SpeedLimitKilometersPerHour = 45, ConnectedBlockIds = ["GREEN-2"] },
            new TrackBlockDefinition { BlockId = "GREEN-2", BlockNumber = 2, Section = "A", LengthMeters = 100, SpeedLimitKilometersPerHour = 45, ConnectedBlockIds = ["GREEN-1"] },
        },
    };

    [TestMethod]
    public void ProductionTrackModelLayout_LoadsWithItsOwnIdsAndDisplayFields()
    {
        var service = new CTCService(new FakeMessageSender());

        var result = service.ApplyTrackLayout(ProductionBlueLayout());

        CollectionAssert.AreEqual(new[] { "Blue" }, result.AddedLineIds);
        var line = service.State.Lines.Single();
        Assert.AreEqual("Blue", line.LineId);
        Assert.AreEqual("Blue Line", line.Name);
        CollectionAssert.AreEqual(Enumerable.Range(1, 15).Select(n => n.ToString()).ToArray(), line.Blocks.Select(block => block.BlockId).ToArray());

        // Section and number stay separate from the ID, so the territory schematic still works.
        var block10 = service.State.FindBlock("10")!;
        Assert.AreEqual("B", block10.Section);
        Assert.AreEqual(10, block10.BlockNumber);
        Assert.IsFalse(string.IsNullOrEmpty(block10.StationName));
        Assert.IsTrue(service.State.FindBlock("5")!.HasSwitch);
        CollectionAssert.AreEquivalent(new[] { "4", "6", "11" }, service.State.FindBlock("5")!.ConnectedBlockIds.ToArray());

        // The Track Model's civil limit (13.89 m/s) reaches CTC's speed planning as 50 km/h.
        Assert.AreEqual(50.0, block10.SpeedLimitKilometersPerHour, 1e-6);
    }

    [TestMethod]
    public void ProductionLayout_LiveStatusResolvesToThatExactBlock()
    {
        var service = new CTCService(new FakeMessageSender());
        service.ApplyTrackLayout(ProductionBlueLayout());

        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "7", Occupancy = OccupancyState.Occupied, ReportedAtUtc = DateTimeOffset.UtcNow });

        Assert.AreEqual(OccupancyState.Occupied, service.State.FindBlock("7")!.Occupancy);
        Assert.IsTrue(service.State.Lines.SelectMany(line => line.Blocks).Where(block => block.BlockId != "7").All(block => block.Occupancy == OccupancyState.Unknown));
        Assert.ThrowsExactly<ArgumentException>(() => service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "B7", Occupancy = OccupancyState.Occupied }),
            "CTC does not translate other naming schemes; an unknown ID is rejected, never guessed.");
    }

    [TestMethod]
    public void ProductionLayout_ScheduleRoutesAndRequestsUseTheSameIds()
    {
        var service = new CTCService(new FakeMessageSender());
        service.ApplyTrackLayout(ProductionBlueLayout());
        BlueLine.ReportAllClear(service);
        var train = new ScheduledTrain { TrainId = "001", LineId = "Blue" };
        string[] route = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "10"];
        for (int i = 0; i < route.Length; i++)
        {
            train.Route.Add(new ScheduledRouteBlock { BlockId = route[i], ArrivalTime = i is 0 or 9 ? Noon + TimeSpan.FromSeconds(60 * i) : null });
        }

        service.QueueSchedule([train]);

        Assert.AreEqual("Blue", service.State.DispatchQueue.Single().LineId);
    }

    [TestMethod]
    public void EquivalentRepeatedLayout_ChangesNothing()
    {
        var sender = new FakeMessageSender();
        var service = BlueLine.CreateService(sender);
        service.QueueSchedule([BlueLine.Train("001", Noon), BlueLine.Train("002", Noon + TimeSpan.FromMinutes(5))]);
        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "A4", Occupancy = OccupancyState.Occupied });
        service.State.Lines[0].TicketSalesPerHour = 12;
        var blockBefore = service.State.FindBlock("A4");
        var changes = new List<CtcStateChangeKind>();
        service.StateChanged += (_, e) => changes.Add(e.Kind);

        var result = service.ApplyTrackLayout(BlueLine.CreateLayout());

        Assert.IsFalse(result.Changed);
        CollectionAssert.AreEqual(new[] { BlueLine.LineId }, result.UnchangedLineIds);
        Assert.IsEmpty(changes, "An equivalent layout raises nothing, so the UI is not rebuilt.");
        Assert.AreSame(blockBefore, service.State.FindBlock("A4"));
        Assert.AreEqual(OccupancyState.Occupied, service.State.FindBlock("A4")!.Occupancy);
        Assert.HasCount(2, service.State.ScheduledTrains);
        Assert.HasCount(2, service.State.DispatchQueue);
        Assert.AreEqual(12, service.State.Lines[0].TicketSalesPerHour);
    }

    [TestMethod]
    public async Task EquivalentRepeatedLayout_KeepsReleasedTrainsAndTheirTracking()
    {
        var service = BlueLine.CreateService();
        service.QueueSchedule([BlueLine.Train("001", Noon)]);
        await service.SetSystemTimeAsync(Noon);
        await BlueLine.PlaceTrainAsync(service, "A1");
        var train = service.State.DispatchedTrains.Single();

        service.ApplyTrackLayout(BlueLine.CreateLayout());

        Assert.AreSame(train, service.State.DispatchedTrains.Single());
        Assert.AreEqual(TrainReleaseStatus.DetectedOnTrack, train.ReleaseStatus);
        Assert.IsNotNull(train.MovementRequest);
    }

    [TestMethod]
    public void AttributeOnlyChange_UpdatesInPlaceAndKeepsStatus()
    {
        var service = BlueLine.CreateService();
        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "A2", Occupancy = OccupancyState.Occupied });
        var layout = BlueLine.CreateLayout();
        layout.Lines[0].Blocks.Single(block => block.BlockId == "A2").StationName = "Station A2";

        var result = service.ApplyTrackLayout(layout);

        CollectionAssert.AreEqual(new[] { BlueLine.LineId }, result.UpdatedLineIds);
        Assert.AreEqual("Station A2", service.State.FindBlock("A2")!.StationName);
        Assert.AreEqual(OccupancyState.Occupied, service.State.FindBlock("A2")!.Occupancy);
    }

    [TestMethod]
    public void UpdatingOneLine_LeavesOtherLinesUntouched()
    {
        var service = new CTCService(new FakeMessageSender());
        var layout = BlueLine.CreateLayout();
        layout.Lines.Add(GreenLine());
        service.ApplyTrackLayout(layout);
        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "GREEN-2", Occupancy = OccupancyState.Occupied });
        var greenBlock = service.State.FindBlock("GREEN-2");

        var revised = BlueLine.CreateLayout();
        revised.Lines[0].Blocks.RemoveAt(revised.Lines[0].Blocks.Count - 1);
        revised.Lines[0].Blocks.Single(block => block.BlockId == "C14").ConnectedBlockIds.Remove("C15");
        revised.Lines.Add(GreenLine());
        var result = service.ApplyTrackLayout(revised);

        CollectionAssert.AreEqual(new[] { BlueLine.LineId }, result.ReplacedLineIds);
        CollectionAssert.AreEqual(new[] { "GREEN" }, result.UnchangedLineIds);
        Assert.AreSame(greenBlock, service.State.FindBlock("GREEN-2"));
        Assert.AreEqual(OccupancyState.Occupied, greenBlock!.Occupancy);
        Assert.IsNull(service.State.FindBlock("C15"));
    }

    [TestMethod]
    public void IncompatibleRevision_NoReleasedTrains_RebuildsLineAndDropsSchedulesThatNoLongerFit()
    {
        var service = BlueLine.CreateService();
        service.QueueSchedule([BlueLine.Train("001", Noon, route: BlueLine.BranchB), BlueLine.Train("002", Noon, route: BlueLine.BranchC)]);
        var changes = new List<CtcStateChangedEventArgs>();
        service.StateChanged += (_, e) => changes.Add(e);

        // Branch C is gone from the new topology.
        var revised = BlueLine.CreateLayout();
        revised.Lines[0].Blocks.RemoveAll(block => block.Section == "C");
        revised.Lines[0].Blocks.Single(block => block.BlockId == "A5").ConnectedBlockIds.Remove("C11");
        var result = service.ApplyTrackLayout(revised);

        CollectionAssert.AreEqual(new[] { BlueLine.LineId }, result.ReplacedLineIds);
        CollectionAssert.AreEqual(new[] { "002" }, result.DroppedScheduledTrainIds);
        Assert.AreEqual("001", service.State.ScheduledTrains.Single().TrainId);
        Assert.AreEqual("001", service.State.DispatchQueue.Single().TrainId);
        Assert.AreEqual(OccupancyState.Unknown, service.State.FindBlock("A1")!.Occupancy, "A rebuilt line starts with unknown status.");
        Assert.IsTrue(changes.Any(change => change.Kind == CtcStateChangeKind.TrackLayout));
        Assert.IsTrue(changes.Any(change => change.Kind == CtcStateChangeKind.Schedule && change.Message!.Contains("Train 002")));
    }

    [TestMethod]
    public async Task IncompatibleRevision_WithReleasedTrain_IsRefusedForThatLine()
    {
        var service = BlueLine.CreateService();
        service.QueueSchedule([BlueLine.Train("001", Noon)]);
        await service.SetSystemTimeAsync(Noon);
        var a1 = service.State.FindBlock("A1");
        var changes = new List<CtcStateChangedEventArgs>();
        service.StateChanged += (_, e) => changes.Add(e);

        var revised = BlueLine.CreateLayout();
        revised.Lines[0].Blocks.Single(block => block.BlockId == "A1").ConnectedBlockIds.Clear();
        revised.Lines.Add(GreenLine());
        var result = service.ApplyTrackLayout(revised);

        Assert.HasCount(1, result.RejectedChanges);
        CollectionAssert.AreEqual(new[] { "GREEN" }, result.AddedLineIds, "Other lines of the same layout are still applied.");
        Assert.AreSame(a1, service.State.FindBlock("A1"), "The line keeps its current topology.");
        CollectionAssert.AreEqual(new[] { "A2" }, a1!.ConnectedBlockIds.ToArray());
        Assert.HasCount(1, service.State.DispatchedTrains);
        Assert.Contains("topology changed while trains are released", changes.Single(change => change.Kind == CtcStateChangeKind.TrackLayoutRejected).Message!);
    }

    [TestMethod]
    public void LineMissingFromLayout_IsRemovedWithItsSchedule()
    {
        var service = BlueLine.CreateService();
        service.QueueSchedule([BlueLine.Train("001", Noon)]);
        var layout = new TrackLayoutMessage { Lines = { GreenLine() } };

        var result = service.ApplyTrackLayout(layout);

        CollectionAssert.AreEqual(new[] { BlueLine.LineId }, result.RemovedLineIds);
        Assert.AreEqual("GREEN", service.State.Lines.Single().LineId);
        Assert.IsEmpty(service.State.ScheduledTrains);
        Assert.IsEmpty(service.State.DispatchQueue);
    }

    [TestMethod]
    public void DuplicateBlockIdAcrossLines_RejectsTheWholeLayout()
    {
        // Two lines numbered from 1 collide; CTC must not silently remap either of them.
        var service = BlueLine.CreateService();
        var layout = BlueLine.CreateLayout();
        layout.Lines.Add(new TrackLineDefinition
        {
            LineId = "GREEN",
            Name = "Green Line",
            Blocks = { new TrackBlockDefinition { BlockId = "A1", BlockNumber = 1, Section = "A", LengthMeters = 100 } },
        });

        var ex = Assert.ThrowsExactly<ArgumentException>(() => service.ApplyTrackLayout(layout));

        Assert.Contains("unique across every line", ex.Message);
        Assert.AreEqual(BlueLine.LineId, service.State.Lines.Single().LineId, "Nothing changed.");
    }

    [TestMethod]
    public void DuplicateLineId_RejectsTheWholeLayout()
    {
        var service = BlueLine.CreateService();
        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "A1", Occupancy = OccupancyState.Occupied });
        var layout = new TrackLayoutMessage { Lines = { new TrackLineDefinition { LineId = "BLUE", Name = "Blue Line", Blocks = { new TrackBlockDefinition { BlockId = "Z1" } } } } };
        layout.Lines.Add(new TrackLineDefinition { LineId = "BLUE", Name = "dup" });

        Assert.ThrowsExactly<ArgumentException>(() => service.ApplyTrackLayout(layout));
        Assert.AreEqual(OccupancyState.Occupied, service.State.FindBlock("A1")!.Occupancy);
    }

    [TestMethod]
    public async Task BlockIdSharedWithALineCtcKeeps_RejectsTheWholeLayout()
    {
        // BLUE has a released train, so it is kept even though the layout omits it; a new line may
        // then not reuse one of its block IDs.
        var service = BlueLine.CreateService();
        service.QueueSchedule([BlueLine.Train("001", Noon)]);
        await service.SetSystemTimeAsync(Noon);
        var layout = new TrackLayoutMessage
        {
            Lines = { new TrackLineDefinition { LineId = "GREEN", Name = "Green Line", Blocks = { new TrackBlockDefinition { BlockId = "A1", LengthMeters = 50 } } } },
        };

        var ex = Assert.ThrowsExactly<ArgumentException>(() => service.ApplyTrackLayout(layout));

        Assert.Contains("would exist on both line GREEN and line BLUE", ex.Message);
        Assert.AreEqual(BlueLine.LineId, service.State.Lines.Single().LineId);
    }

    [TestMethod]
    public void BlankBlockId_IsRejected()
    {
        var service = new CTCService(new FakeMessageSender());
        var layout = new TrackLayoutMessage { Lines = { new TrackLineDefinition { LineId = "BLUE", Blocks = { new TrackBlockDefinition { BlockId = " " } } } } };

        Assert.ThrowsExactly<ArgumentException>(() => service.ApplyTrackLayout(layout));
        Assert.IsEmpty(service.State.Lines);
    }
}
