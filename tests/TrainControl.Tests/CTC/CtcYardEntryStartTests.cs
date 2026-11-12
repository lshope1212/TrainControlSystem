using CTC.Core.Models;
using CTC.Core.Scheduling;
using CTC.Core.Services;
using TrackModel.Core.Services;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.CTC;

/// <summary>
/// Yard-restricted starting blocks: each train starts its route at one of its line's yard entries,
/// which come only from the Track Model layout (<see cref="TrackBlockDefinition.IsYardEntry"/>).
/// Covers the Schedule Builder template, the converter, the CSV import, <see cref="CTCService.QueueSchedule"/>,
/// layout revisions, the dispatch request, and the production course data.
/// </summary>
/// <remarks>
/// The multi-entry cases use a Blue fixture with a second, test-only yard entry at C11; the real
/// Blue Line has one (block 1).
/// </remarks>
[TestClass]
public class CtcYardEntryStartTests
{
    private static readonly string[] TwoEntries = ["A1", "C11"];

    private static TimeSpan At(int hours, int minutes, int seconds) => new(hours, minutes, seconds);

    private static CtcLineState Blue(CTCService service) => service.State.FindLine(BlueLine.LineId)!;

    private static ScheduleTemplateRow Row(ScheduleTemplate template, string blockId) => template.Rows.Single(row => row.BlockId == blockId);

    private static ScheduleTemplate Template(CTCService service, int trainCount = 1) => ScheduleTemplateFactory.Create(Blue(service), trainCount);

    // ---- Starting-block selection ----

    [TestMethod]
    public void SingleYardEntry_IsSelectedForEveryTrain()
    {
        var template = Template(BlueLine.CreateService(), trainCount: 3);

        CollectionAssert.AreEqual(new[] { "A1" }, template.YardEntryBlockIds.ToArray());
        for (int column = 0; column < 3; column++)
        {
            Assert.AreEqual("A1", template.GetStartBlockId(column));
            Assert.IsTrue(Row(template, "A1").IsRouteStartFor(column));
        }
    }

    [TestMethod]
    public void MultipleYardEntries_AreNotPreselected_AndEachTrainChoosesIndependently()
    {
        var service = BlueLine.CreateService(yardEntries: TwoEntries);
        var template = Template(service, trainCount: 2);

        CollectionAssert.AreEqual(TwoEntries, template.YardEntryBlockIds.ToArray());
        Assert.AreEqual(string.Empty, template.GetStartBlockId(0), "With a choice to make, nothing is assumed.");

        Assert.IsTrue(template.SelectStartBlock(0, "A1").IsAccepted);
        Assert.IsTrue(template.SelectStartBlock(1, "C11").IsAccepted);
        Row(template, "A1").SetTrainTime(0, "12:00:00");
        Row(template, "B10").SetTrainTime(0, "12:05:00");
        Row(template, "C11").SetTrainTime(1, "12:01:00");
        Row(template, "C15").SetTrainTime(1, "12:04:00");

        var result = ScheduleTemplateConverter.Convert(template);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        CollectionAssert.AreEqual(BlueLine.BranchB, result.Trains[0].Route.Select(block => block.BlockId).ToArray());
        CollectionAssert.AreEqual(new[] { "C11", "C12", "C13", "C14", "C15" }, result.Trains[1].Route.Select(block => block.BlockId).ToArray());
        Assert.AreEqual("A1", result.Trains[0].StartBlockId);
        Assert.AreEqual("C11", result.Trains[1].StartBlockId);
        service.QueueSchedule(result.Trains);
        Assert.HasCount(2, service.State.DispatchQueue);
    }

    [TestMethod]
    public void NonYardBlock_CannotBeSelected()
    {
        var template = Template(BlueLine.CreateService(yardEntries: TwoEntries));

        var change = template.SelectStartBlock(0, "A3");

        Assert.IsFalse(change.IsAccepted);
        Assert.AreEqual("Train 001 cannot start at A3: trains enter Blue Line from the yard only at A1, C11.", change.Message);
        Assert.AreEqual(string.Empty, template.GetStartBlockId(0));
        Assert.IsFalse(template.SelectStartBlock(0, "NOPE").IsAccepted);
    }

    [TestMethod]
    public void NonYardStart_CannotBeQueued()
    {
        var service = BlueLine.CreateService();

        var ex = Assert.Throws<ArgumentException>(() => service.QueueSchedule([BlueLine.Train("001", At(12, 0, 0), route: ["A3", "A4", "A5"])]));

        Assert.AreEqual("Train 001 cannot start at A3: it is not a yard entry. Trains enter Blue Line from the yard only at A1.", ex.Message);
        Assert.IsEmpty(service.State.ScheduledTrains);
        Assert.IsEmpty(service.State.DispatchQueue);
    }

    [TestMethod]
    public void StartBlockOnAnotherLine_IsRejected()
    {
        var service = BlueLine.CreateTwoLineService();
        var train = BlueLine.Train("001", At(12, 0, 0), route: ["G-A1", "G-A2", "G-A3"]);
        train.LineId = BlueLine.LineId;

        var ex = Assert.Throws<ArgumentException>(() => service.QueueSchedule([train]));

        Assert.AreEqual("Train 001's starting block G-A1 is on Green Line, not Blue Line. Choose one of Blue Line's yard entries: A1.", ex.Message);
    }

    [TestMethod]
    public void MissingYardEntryMetadata_PreventsScheduling_WithAClearReason()
    {
        // A producer that sends no yard-entry data (e.g. an older Track Model): CTC invents none.
        var service = BlueLine.CreateService(yardEntries: []);
        var template = Template(service);
        Row(template, "A1").SetTrainTime(0, "12:00:00");
        Row(template, "B10").SetTrainTime(0, "12:05:00");

        Assert.IsEmpty(template.YardEntryBlockIds);
        Assert.AreEqual(string.Empty, template.GetStartBlockId(0), "Not the lowest-numbered block, nor the first row.");
        var conversion = ScheduleTemplateConverter.Convert(template);
        Assert.IsFalse(conversion.IsSuccess);
        StringAssert.StartsWith(conversion.ErrorMessage, "Blue Line has no yard-entry blocks in its track layout");

        var queue = Assert.Throws<ArgumentException>(() => service.QueueSchedule([BlueLine.Train("001", At(12, 0, 0))]));
        StringAssert.StartsWith(queue.Message, "Train 001 cannot be scheduled: Blue Line has no yard-entry blocks in its track layout");

        var import = ScheduleCsvImporter.Import(Blue(service), "Line,Section,Block Number,Train 1\nBlue,A,1,12:00:00\nBlue,B,10,12:05:00\n");
        Assert.IsFalse(import.IsSuccess);
        StringAssert.StartsWith(import.ErrorMessage, "Blue Line has no yard-entry blocks in its track layout");
    }

    [TestMethod]
    public void UnselectedStart_IsRejectedByTheConverter()
    {
        var template = Template(BlueLine.CreateService(yardEntries: TwoEntries));
        Row(template, "A1").SetTrainTime(0, "12:00:00");
        Row(template, "B10").SetTrainTime(0, "12:05:00");

        var result = ScheduleTemplateConverter.Convert(template);

        Assert.AreEqual("Train 001 has no starting block. Choose one of Blue Line's yard entries: A1, C11.", result.ErrorMessage);
    }

    // ---- Scheduling ----

    [TestMethod]
    public void DepartureTime_IsReadFromTheSelectedStart_AndTheRouteBeginsThere()
    {
        // Train starts on branch C and runs to A1: the route is C11 -> A5 -> ... -> A1, with no block before C11.
        var template = Template(BlueLine.CreateService(yardEntries: TwoEntries));
        template.SelectStartBlock(0, "C11");
        Row(template, "C11").SetTrainTime(0, "12:00:00");
        Row(template, "A3").SetTrainTime(0, "12:02:00");
        Row(template, "A1").SetTrainTime(0, "12:04:00");

        var train = ScheduleTemplateConverter.Convert(template).Trains.Single();

        Assert.AreEqual("C11", train.Route[0].BlockId);
        Assert.AreEqual("C11", train.StartBlockId);
        Assert.AreEqual(At(12, 0, 0), train.DepartureTime);
        CollectionAssert.AreEqual(new[] { "C11", "A5", "A4", "A3", "A2", "A1" }, train.Route.Select(block => block.BlockId).ToArray());
        CollectionAssert.AreEqual(new TimeSpan?[] { At(12, 0, 0), null, null, At(12, 2, 0), null, At(12, 4, 0) }, train.Route.Select(block => block.ArrivalTime).ToArray());
        Assert.AreEqual(TimeSpan.Zero, train.Route[0].DwellTime, "The starting block never dwells.");
    }

    [TestMethod]
    public void ArrivalAndDwellTimes_StillWork_FromASelectedStart()
    {
        var layout = BlueLine.CreateLayout(yardEntries: TwoEntries);
        layout.Lines[0].Blocks.Single(block => block.BlockId == "C13").StationName = "Station C13";
        var service = new CTCService(new FakeMessageSender());
        service.ApplyTrackLayout(layout);
        var template = Template(service);
        template.SelectStartBlock(0, "C11");
        Row(template, "C11").SetTrainTime(0, "12:00:00");
        Row(template, "C13").SetTrainTime(0, "12:01:00");
        Row(template, "C15").SetTrainTime(0, "12:03:00");
        Assert.AreEqual("45", Row(template, "C13").TrainDwellTimes[0], "A station stop gets the default dwell.");
        Assert.AreEqual(string.Empty, Row(template, "C11").TrainDwellTimes[0], "The start has no dwell.");
        Assert.IsNotNull(Row(template, "C11").TrySetTrainDwellTime(0, "30"), "A dwell at the start is refused.");
        Assert.IsNull(Row(template, "C13").TrySetTrainDwellTime(0, "60"));

        var train = ScheduleTemplateConverter.Convert(template).Trains.Single();

        Assert.AreEqual(TimeSpan.FromSeconds(60), train.Route.Single(block => block.BlockId == "C13").DwellTime);
        Assert.AreEqual(TimeSpan.Zero, train.Route.Single(block => block.BlockId == "C15").DwellTime);
    }

    [TestMethod]
    public void ChangingTheStart_MovesTheDeparture_FlagsAffectedTimes_AndRevalidatesTheRoute()
    {
        var template = Template(BlueLine.CreateService(yardEntries: TwoEntries));
        template.SelectStartBlock(0, "A1");
        Row(template, "A1").SetTrainTime(0, "12:00:00");
        Row(template, "A3").SetTrainTime(0, "12:01:00");
        Row(template, "B10").SetTrainTime(0, "12:05:00");

        var change = template.SelectStartBlock(0, "C11");

        Assert.IsTrue(change.IsChanged);
        StringAssert.Contains(change.Message, "its departure time 12:00:00 moved from A1 to C11");
        Assert.AreEqual(string.Empty, Row(template, "A1").TrainTimes[0], "The old departure is not left behind as an arrival time at A1.");
        Assert.AreEqual("12:00:00", Row(template, "C11").TrainTimes[0]);
        Assert.IsTrue(template.NeedsReconsidering(Row(template, "C11"), 0));
        Assert.AreEqual("12:01:00", Row(template, "A3").TrainTimes[0], "Other entered times are kept.");

        // From C11, A3 then B10 would need the train to reverse through A5: the route is rebuilt and refused, not guessed.
        var result = ScheduleTemplateConverter.Convert(template);
        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.ErrorMessage, "cannot reach B10 from A3");

        // Editing the moved time marks it reconsidered.
        Row(template, "C11").SetTrainTime(0, "12:00:10");
        Assert.IsFalse(template.NeedsReconsidering(Row(template, "C11"), 0));
    }

    [TestMethod]
    public void ChangingTheStart_ToABlockWithAnArrivalTime_NeedsTheDispatcherToConfirmIt()
    {
        var service = BlueLine.CreateService(yardEntries: ["A1", "A3"]);
        var template = Template(service);
        template.SelectStartBlock(0, "A1");
        Row(template, "A3").SetTrainTime(0, "12:01:00");
        Row(template, "B10").SetTrainTime(0, "12:05:00");
        Assert.AreEqual("0", Row(template, "A3").TrainDwellTimes[0]);

        // No departure was entered at A1; A3's time was an ARRIVAL time and is not silently reused.
        var change = template.SelectStartBlock(0, "A3");

        StringAssert.Contains(change.Message, "was entered as an arrival time; re-enter it to confirm it as the departure time");
        Assert.IsTrue(template.IsDepartureUnconfirmed(0));
        Assert.AreEqual(string.Empty, Row(template, "A3").TrainDwellTimes[0]);
        StringAssert.Contains(ScheduleTemplateConverter.Convert(template).ErrorMessage, "was entered as an arrival time before that block became its start");

        Row(template, "A3").SetTrainTime(0, "12:01:00");
        var train = ScheduleTemplateConverter.Convert(template).Trains.Single();
        Assert.AreEqual("A3", train.StartBlockId);
        Assert.AreEqual(At(12, 1, 0), train.DepartureTime);
    }

    [TestMethod]
    public void ChangingTheStart_ReportsAReplacedArrival_AndFlagsArrivalsNotAfterTheDeparture()
    {
        var template = Template(BlueLine.CreateService(yardEntries: ["A1", "A3"]));
        template.SelectStartBlock(0, "A3");
        Row(template, "A3").SetTrainTime(0, "12:02:00");
        Row(template, "A1").SetTrainTime(0, "12:00:00");
        Row(template, "B7").SetTrainTime(0, "12:01:30");

        var change = template.SelectStartBlock(0, "A1");

        // The 12:02 departure moved to A1, replacing A1's arrival time; nothing is dropped without saying so.
        Assert.AreEqual("12:02:00", Row(template, "A1").TrainTimes[0]);
        Assert.AreEqual(string.Empty, Row(template, "A3").TrainTimes[0]);
        StringAssert.Contains(change.Message, "the arrival time 12:00:00 it had at A1 was replaced");
        StringAssert.Contains(change.Message, "its arrival at B7 (12:01:30) is not after the departure");
        Assert.IsTrue(template.NeedsReconsidering(Row(template, "B7"), 0));
        StringAssert.Contains(ScheduleTemplateConverter.Convert(template).ErrorMessage, "not later than its departure from A1");
    }

    // ---- CSV import ----

    [TestMethod]
    public void CsvImport_TakesTheOnlyTimedYardEntryAsTheStart()
    {
        var service = BlueLine.CreateService(yardEntries: TwoEntries);
        const string csv = "Line,Section,Block Number,Train 1,Train 2\n"
            + "Blue,A,1,12:00:00,\n"
            + "Blue,B,10,12:05:00,\n"
            + "Blue,C,11,,12:01:00\n"
            + "Blue,C,15,,12:04:00\n";

        var import = ScheduleCsvImporter.Import(Blue(service), csv);

        Assert.IsTrue(import.IsSuccess, import.ErrorMessage);
        Assert.IsEmpty(import.TrainsNeedingStartSelection);
        Assert.AreEqual("A1", import.Template!.GetStartBlockId(0));
        Assert.AreEqual("C11", import.Template.GetStartBlockId(1));
        var trains = ScheduleTemplateConverter.Convert(import.Template).Trains;
        CollectionAssert.AreEqual(new[] { "A1", "C11" }, trains.Select(train => train.StartBlockId).ToArray());
    }

    [TestMethod]
    public void CsvImport_AmbiguousStart_RequiresSelection_AndNeverUsesTheFirstTimedBlock()
    {
        var service = BlueLine.CreateService(yardEntries: TwoEntries);

        // Two timed yard entries: ambiguous. No timed yard entry (first timed block A3): also not guessed.
        var ambiguous = ScheduleCsvImporter.Import(Blue(service), "Line,Section,Block Number,Train 1,Train 2\nBlue,A,1,12:00:00,\nBlue,A,3,,12:00:00\nBlue,A,5,12:02:00,12:02:00\nBlue,C,11,12:04:00,\n");

        Assert.IsTrue(ambiguous.IsSuccess, ambiguous.ErrorMessage);
        CollectionAssert.AreEqual(new[] { "001", "002" }, ambiguous.TrainsNeedingStartSelection.ToArray());
        Assert.AreEqual(string.Empty, ambiguous.Template!.GetStartBlockId(0));
        Assert.AreEqual(string.Empty, ambiguous.Template.GetStartBlockId(1));
        StringAssert.Contains(ScheduleTemplateConverter.Convert(ambiguous.Template).ErrorMessage, "has no starting block");

        // The dispatcher selects it after import, as in a generated template.
        ambiguous.Template.SelectStartBlock(0, "A1");
        StringAssert.Contains(ScheduleTemplateConverter.Convert(ambiguous.Template).ErrorMessage, "Train 002 has no starting block");
    }

    [TestMethod]
    public void CsvImport_OnASingleEntryLine_StartsThere_EvenWhenAnotherBlockIsTimedFirst()
    {
        var service = BlueLine.CreateService();

        var import = ScheduleCsvImporter.Import(Blue(service), "Line,Section,Block Number,Train 1\nBlue,A,3,12:00:00\nBlue,B,10,12:05:00\n");

        Assert.IsTrue(import.IsSuccess, import.ErrorMessage);
        Assert.AreEqual("A1", import.Template!.GetStartBlockId(0));
        Assert.AreEqual("Train 001 is missing its departure time at its starting block A1.", ScheduleTemplateConverter.Convert(import.Template).ErrorMessage,
            "A3 is not treated as a starting block just because it is timed first.");
    }

    // ---- Layout changes ----

    [TestMethod]
    public void ChangedLayout_DropsAQueuedScheduleWhoseStartIsNoLongerAYardEntry()
    {
        var service = BlueLine.CreateService(yardEntries: TwoEntries);
        service.QueueSchedule([BlueLine.Train("001", At(12, 0, 0)), BlueLine.Train("002", At(12, 0, 0), route: ["C11", "C12", "C13"])]);

        var result = service.ApplyTrackLayout(BlueLine.CreateLayout());

        CollectionAssert.AreEqual(new[] { BlueLine.LineId }, result.UpdatedLineIds.ToArray(), "Only an attribute changed; the topology is the same.");
        CollectionAssert.AreEqual(new[] { "002" }, result.InvalidStartTrainIds.ToArray());
        StringAssert.Contains(result.Summary, "Schedules dropped because their start block is no longer a yard entry: Train 002");
        CollectionAssert.AreEqual(new[] { "001" }, service.State.ScheduledTrains.Select(train => train.TrainId).ToArray());
        CollectionAssert.AreEqual(new[] { "001" }, service.State.DispatchQueue.Select(entry => entry.TrainId).ToArray());
        Assert.IsFalse(service.State.FindBlock("C11")!.IsYardEntry);
    }

    [TestMethod]
    public void ChangedLayout_RejectsAnObsoleteTemplateSelection_AtQueueTime()
    {
        var service = BlueLine.CreateService(yardEntries: TwoEntries);
        var template = Template(service);
        template.SelectStartBlock(0, "C11");
        Row(template, "C11").SetTrainTime(0, "12:00:00");
        Row(template, "C15").SetTrainTime(0, "12:04:00");
        var trains = ScheduleTemplateConverter.Convert(template).Trains;

        service.ApplyTrackLayout(BlueLine.CreateLayout());

        var ex = Assert.Throws<ArgumentException>(() => service.QueueSchedule(trains));
        Assert.AreEqual("Train 001 cannot start at C11: it is not a yard entry. Trains enter Blue Line from the yard only at A1.", ex.Message);
    }

    [TestMethod]
    public async Task ChangedLayout_KeepsAReleasedTrainFromAFormerYardEntry()
    {
        var service = BlueLine.CreateService(yardEntries: TwoEntries);
        service.QueueSchedule([BlueLine.Train("002", At(12, 0, 0), route: ["C11", "C12", "C13"])]);
        await service.SetSystemTimeAsync(At(12, 0, 0));
        Assert.IsNotNull(service.State.FindDispatchedTrain("002"));

        var result = service.ApplyTrackLayout(BlueLine.CreateLayout());

        Assert.IsEmpty(result.InvalidStartTrainIds, "A released train is on the track; its schedule stays.");
        Assert.HasCount(1, service.State.ScheduledTrains);
    }

    // ---- Dispatch ----

    [TestMethod]
    public async Task DispatchRequest_CarriesTheSelectedStartBlock()
    {
        var sender = new FakeMessageSender();
        var service = BlueLine.CreateService(sender, yardEntries: TwoEntries);
        var template = Template(service);
        template.SelectStartBlock(0, "C11");
        Row(template, "C11").SetTrainTime(0, "12:00:00");
        Row(template, "C15").SetTrainTime(0, "12:04:00");
        service.QueueSchedule(ScheduleTemplateConverter.Convert(template).Trains);

        await service.SetSystemTimeAsync(At(12, 0, 0));

        var dispatch = sender.DispatchMessages.OfType<TrainDispatchRequestMessage>().Single();
        Assert.AreEqual("C11", dispatch.StartBlockId);
        CollectionAssert.AreEqual(new[] { "C11", "C12", "C13", "C14", "C15" }, dispatch.RouteBlockIds.ToArray());
        Assert.AreEqual("C11", service.State.FindDispatchedTrain("001")!.CurrentBlockId);
        Assert.AreEqual(OccupancyState.Clear, service.State.FindBlock("C11")!.Occupancy, "Scheduling or releasing never marks the start block occupied; only the wayside does.");
    }

    // ---- Production course data ----

    [TestMethod]
    public void CourseLayout_YardEntries_AreBlue1_Red9_AndGreen63Only()
    {
        var service = new CTCService(new FakeMessageSender());
        var model = new TrackService();
        CourseTrackLayout.Load(model);
        service.ApplyTrackLayout(model.CreateLayoutMessage());

        string[] Entries(string lineId) => YardEntries.For(service.State.FindLine(lineId)!).Select(block => block.BlockId).ToArray();
        CollectionAssert.AreEqual(new[] { "1" }, Entries("Blue"));
        CollectionAssert.AreEqual(new[] { "RED-9" }, Entries("Red"));
        CollectionAssert.AreEqual(new[] { "GREEN-63" }, Entries("Green"), "Not the yard switch blocks 58/62, not 57 (the way into the yard), not 1 or 14.");
        Assert.AreEqual("K63 (GREEN-63)", YardEntries.Label(service.State.FindBlock("GREEN-63")!));
    }

    [TestMethod]
    public void GreenYardToEdgebrook_IsRoutedInTheDirectionOfTravelAndRoundTheLoop_HoweverItIsTimed()
    {
        var service = new CTCService(new FakeMessageSender());
        var model = new TrackService();
        CourseTrackLayout.Load(model);
        service.ApplyTrackLayout(model.CreateLayoutMessage());
        var green = service.State.FindLine("Green")!;

        // The three schedules that used to expose the connectivity-only planner (backwards up J-I-H-G, leg to leg
        // through switch 77, and no way round N twice) now all give the one legal route.
        var direct = ScheduleTemplateFactory.Create(green, 1);
        Row(direct, "GREEN-63").SetTrainTime(0, "14:00:00");
        Row(direct, "GREEN-9").SetTrainTime(0, "15:00:00");

        var forward = ScheduleTemplateFactory.Create(green, 1);
        Row(forward, "GREEN-63").SetTrainTime(0, "14:00:00");
        Row(forward, "GREEN-120").SetTrainTime(0, "14:30:00");
        Row(forward, "GREEN-9").SetTrainTime(0, "15:00:00");

        var loop = ScheduleTemplateFactory.Create(green, 1);
        Row(loop, "GREEN-63").SetTrainTime(0, "14:00:00");
        Row(loop, "GREEN-90").SetTrainTime(0, "14:10:00");
        Row(loop, "GREEN-120").SetTrainTime(0, "14:30:00");
        Row(loop, "GREEN-9").SetTrainTime(0, "15:00:00");

        foreach (var template in new[] { direct, forward, loop })
        {
            var result = ScheduleTemplateConverter.Convert(template);
            Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
            var train = result.Trains.Single();
            Assert.AreEqual("GREEN-64", train.Route[1].BlockId, "Out of the yard in the increasing direction.");
            CollectionAssert.AreEqual(CtcGreenLineRoutingTests.YardToEdgebrook, train.Route.Select(block => block.BlockId).ToArray());
            Assert.IsEmpty(RouteGeometry.FindProblems(green, train));
        }
    }

    [TestMethod]
    public void RouteGeometry_FindsNothingWrongWithARouteInTheDirectionOfTravel()
    {
        var service = new CTCService(new FakeMessageSender());
        var model = new TrackService();
        CourseTrackLayout.Load(model);
        service.ApplyTrackLayout(model.CreateLayoutMessage());
        var train = new ScheduledTrain { TrainId = "001", LineId = "Green" };
        foreach (var id in new[] { "GREEN-63", "GREEN-64", "GREEN-65", "GREEN-66" })
        {
            train.Route.Add(new ScheduledRouteBlock { BlockId = id });
        }

        Assert.IsEmpty(RouteGeometry.FindProblems(service.State.FindLine("Green")!, train));
    }
}
