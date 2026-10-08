using CTC.Core.Models;
using CTC.Core.Scheduling;
using CTC.Core.Services;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.CTC;

[TestClass]
public class CtcScheduleTests
{
    private static ScheduleTemplate CreateTemplate(int trainCount) =>
        ScheduleTemplateFactory.Create(BlueLine.CreateService().State.FindLine(BlueLine.LineId)!, trainCount);

    private static ScheduleTemplateRow Row(ScheduleTemplate template, string blockId) =>
        template.Rows.Single(row => row.BlockId == blockId);

    /// <summary>Fills one train column: the train enters each block 4 s after the previous one, from 12:00:00.</summary>
    private static void FillRoute(ScheduleTemplate template, int column, IEnumerable<string> blockIds, int startSecond = 0)
    {
        int second = startSecond;
        foreach (var blockId in blockIds)
        {
            Row(template, blockId).TrainTimes[column] = $"12:{second / 60:00}:{second % 60:00}";
            second += 4;
        }
    }

    private static string[] RouteOf(ScheduledTrain train) => train.Route.Select(block => block.BlockId).ToArray();

    [TestMethod]
    public void Create_ThreeTrains_ProducesThreeTrainColumns()
    {
        var template = CreateTemplate(3);

        CollectionAssert.AreEqual(new[] { "000", "001", "002" }, template.TrainIds.ToArray());
        Assert.HasCount(15, template.Rows);
        Assert.IsTrue(template.Rows.All(row => row.TrainTimes.Length == 3 && row.TrainTimes.All(string.IsNullOrEmpty)));
    }

    [TestMethod]
    public void Create_MarksLowestBlockAsStartAndCopiesTrackData()
    {
        // Blocks deliberately out of number order to check the start block is not just "first row".
        var service = new CTCService(new FakeMessageSender());
        service.ApplyTrackLayout(new TrackLayoutMessage
        {
            Lines =
            {
                new TrackLineDefinition
                {
                    LineId = "BLUE",
                    Blocks =
                    {
                        new TrackBlockDefinition { BlockId = "B2", BlockNumber = 2, HasCrossing = true, ConnectedBlockIds = { "B1" } },
                        new TrackBlockDefinition { BlockId = "B1", BlockNumber = 1, LengthMeters = 50, SpeedLimitKilometersPerHour = 50, ConnectedBlockIds = { "B2" } },
                        new TrackBlockDefinition { BlockId = "B10", BlockNumber = 10, StationName = "Station B", HasSignal = true },
                    },
                },
            },
        });

        var template = ScheduleTemplateFactory.Create(service.State.FindLine("BLUE")!, 1);

        Assert.IsTrue(Row(template, "B1").IsRouteStart);
        Assert.IsFalse(Row(template, "B2").IsRouteStart);
        Assert.AreEqual(50.0, Row(template, "B1").LengthMeters);
        Assert.AreEqual(50.0, Row(template, "B1").SpeedLimitKilometersPerHour);
        CollectionAssert.AreEqual(new[] { "B2" }, Row(template, "B1").ConnectedBlockIds.ToArray());
        Assert.AreEqual("Station B; Signal", Row(template, "B10").Infrastructure);
        Assert.AreEqual("Railway Crossing", Row(template, "B2").Infrastructure);
    }

    [TestMethod]
    public void Convert_TimeOnEveryRouteBlock_BecomesTimedRoute()
    {
        // Every block row, station or not, accepts a time.
        var template = CreateTemplate(1);
        FillRoute(template, 0, BlueLine.BranchB);

        var result = ScheduleTemplateConverter.Convert(template);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        var train = result.Trains.Single();
        Assert.AreEqual("000", train.TrainId);
        Assert.AreEqual("BLUE", train.LineId);
        CollectionAssert.AreEqual(BlueLine.BranchB, RouteOf(train));
        Assert.IsTrue(train.Route.All(block => block.IsTimed));
        Assert.AreEqual(new TimeSpan(12, 0, 8), train.Route[2].ArrivalTime!.Value);
    }

    [TestMethod]
    public void Convert_StartBlockTime_BecomesDepartureTime()
    {
        var template = CreateTemplate(1);
        FillRoute(template, 0, BlueLine.BranchB, startSecond: 30);

        var train = ScheduleTemplateConverter.Convert(template).Trains.Single();

        Assert.AreEqual("A1", train.StartBlockId);
        Assert.AreEqual(new TimeSpan(12, 0, 30), train.DepartureTime);
    }

    [TestMethod]
    public void Convert_SparseTimes_RoutesThroughBlankBlocks()
    {
        // Only the start and the end station are timed.
        var template = CreateTemplate(1);
        Row(template, "A1").TrainTimes[0] = "12:00:00";
        Row(template, "B10").TrainTimes[0] = "12:00:36";

        var result = ScheduleTemplateConverter.Convert(template);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        var train = result.Trains.Single();
        CollectionAssert.AreEqual(BlueLine.BranchB, RouteOf(train));
        Assert.AreEqual(new TimeSpan(12, 0, 0), train.Route[0].ArrivalTime!.Value);
        Assert.AreEqual(new TimeSpan(12, 0, 36), train.Route[^1].ArrivalTime!.Value);
        Assert.IsTrue(train.Route.Skip(1).SkipLast(1).All(block => !block.IsTimed));
    }

    [TestMethod]
    public void Convert_SeveralWaypoints_KeepsTheirTimes()
    {
        var template = CreateTemplate(1);
        Row(template, "A1").TrainTimes[0] = "12:00:00";
        Row(template, "A4").TrainTimes[0] = "12:00:12";
        Row(template, "B8").TrainTimes[0] = "12:00:28";

        var train = ScheduleTemplateConverter.Convert(template).Trains.Single();

        CollectionAssert.AreEqual(new[] { "A1", "A2", "A3", "A4", "A5", "B6", "B7", "B8" }, RouteOf(train));
        CollectionAssert.AreEqual(
            new[] { "A1", "A4", "B8" },
            train.Route.Where(block => block.IsTimed).Select(block => block.BlockId).ToArray());
    }

    [TestMethod]
    public void Convert_TimedBlockOnBranch_SelectsThatBranch()
    {
        // Train 000 is timed at B10 and Train 001 at C15; each leaves the other branch out.
        var template = CreateTemplate(2);
        Row(template, "A1").TrainTimes[0] = "12:00:00";
        Row(template, "B10").TrainTimes[0] = "12:00:36";
        Row(template, "A1").TrainTimes[1] = "12:01:00";
        Row(template, "C15").TrainTimes[1] = "12:01:36";

        var result = ScheduleTemplateConverter.Convert(template);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        CollectionAssert.AreEqual(BlueLine.BranchB, RouteOf(result.Trains[0]));
        CollectionAssert.AreEqual(BlueLine.BranchC, RouteOf(result.Trains[1]));
    }

    [TestMethod]
    public void Convert_RouteEndsAtLastTimedBlock()
    {
        var template = CreateTemplate(1);
        Row(template, "A1").TrainTimes[0] = "12:00:00";
        Row(template, "A3").TrainTimes[0] = "12:00:08";

        var result = ScheduleTemplateConverter.Convert(template);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        CollectionAssert.AreEqual(new[] { "A1", "A2", "A3" }, RouteOf(result.Trains.Single()));
    }

    [TestMethod]
    public void Convert_BothBranches_Fails()
    {
        // B10 then C15 would mean reversing back through the switch at A5.
        var template = CreateTemplate(1);
        Row(template, "A1").TrainTimes[0] = "12:00:00";
        Row(template, "B10").TrainTimes[0] = "12:00:36";
        Row(template, "C15").TrainTimes[0] = "12:01:30";

        var result = ScheduleTemplateConverter.Convert(template);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(
            "Train 000 cannot reach C15 from B10 along connected blocks without reversing. Check the times are in travel order and on one branch.",
            result.ErrorMessage);
        Assert.IsEmpty(result.Trains);
    }

    [TestMethod]
    public void Convert_JumpingBetweenBranches_Fails()
    {
        // C11 is timed between A5 and B6, so the train would have to come back from C11 to B6.
        var template = CreateTemplate(1);
        FillRoute(template, 0, BlueLine.BranchB);
        Row(template, "C11").TrainTimes[0] = "12:00:18";

        var result = ScheduleTemplateConverter.Convert(template);

        Assert.IsFalse(result.IsSuccess);
        Assert.Contains("cannot reach B6 from C11", result.ErrorMessage!);
    }

    [TestMethod]
    public void Convert_TimesOutOfTravelOrder_Fails()
    {
        // A4 is timed before A2, but the train must pass A2 to reach A4.
        var template = CreateTemplate(1);
        Row(template, "A1").TrainTimes[0] = "12:00:00";
        Row(template, "A2").TrainTimes[0] = "12:00:20";
        Row(template, "A4").TrainTimes[0] = "12:00:10";

        var result = ScheduleTemplateConverter.Convert(template);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(
            "Train 000 passes A2 on the way from A1 to A4, but is scheduled to enter A2 later, at 12:00:20. Times must increase along the route.",
            result.ErrorMessage);
    }

    [TestMethod]
    public void Convert_EqualTimes_Fails()
    {
        var template = CreateTemplate(1);
        FillRoute(template, 0, BlueLine.BranchB);
        Row(template, "A3").TrainTimes[0] = Row(template, "A2").TrainTimes[0];

        var result = ScheduleTemplateConverter.Convert(template);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("Train 000 is scheduled to enter both A2 and A3 at 12:00:04. Times must increase along the route.", result.ErrorMessage);
    }

    [TestMethod]
    public void Convert_TimeBeforeDeparture_Fails()
    {
        var template = CreateTemplate(1);
        FillRoute(template, 0, BlueLine.BranchB);
        Row(template, "B7").TrainTimes[0] = "11:59:00";

        var result = ScheduleTemplateConverter.Convert(template);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(
            "Train 000 is scheduled to enter B7 at 11:59:00, which is not later than its departure from A1 at 12:00:00.",
            result.ErrorMessage);
    }

    [TestMethod]
    public void Convert_MissingStartTime_Fails()
    {
        var template = CreateTemplate(2);
        FillRoute(template, 0, BlueLine.BranchB);
        FillRoute(template, 1, BlueLine.BranchB.Skip(1));

        var result = ScheduleTemplateConverter.Convert(template);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("Train 001 is missing a route start time.", result.ErrorMessage);
        Assert.IsEmpty(result.Trains);
    }

    [TestMethod]
    public void Convert_InvalidTimeText_Fails()
    {
        var template = CreateTemplate(1);
        FillRoute(template, 0, BlueLine.BranchB);
        Row(template, "A4").TrainTimes[0] = "12:8";

        var result = ScheduleTemplateConverter.Convert(template);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("Train 000 has an invalid time '12:8' at A4. Use HH:mm:ss.", result.ErrorMessage);
    }

    [TestMethod]
    public void Convert_OnlyStartTime_Fails()
    {
        var template = CreateTemplate(1);
        Row(template, "A1").TrainTimes[0] = "12:00:00";

        var result = ScheduleTemplateConverter.Convert(template);

        Assert.IsFalse(result.IsSuccess);
        Assert.Contains("only a route start time", result.ErrorMessage!);
    }

    [TestMethod]
    public void QueueSchedule_CreatesOneQueueEntryPerTrain()
    {
        var service = BlueLine.CreateService();

        service.QueueSchedule([BlueLine.Train("000", new TimeSpan(12, 0, 0)), BlueLine.Train("001", new TimeSpan(12, 3, 0))]);

        Assert.HasCount(2, service.State.ScheduledTrains);
        Assert.HasCount(2, service.State.DispatchQueue);
        Assert.IsTrue(service.State.DispatchQueue.All(entry => entry.LineId == "BLUE" && entry.QueueStatus == DispatchQueueStatus.Queued));
    }

    [TestMethod]
    public void QueueSchedule_SortsQueueByDepartureTime()
    {
        var service = BlueLine.CreateService();

        service.QueueSchedule(
        [
            BlueLine.Train("000", new TimeSpan(12, 6, 0)),
            BlueLine.Train("001", new TimeSpan(12, 0, 0)),
            BlueLine.Train("002", new TimeSpan(12, 3, 0)),
        ]);

        CollectionAssert.AreEqual(
            new[] { "001", "002", "000" },
            service.State.DispatchQueue.Select(entry => entry.TrainId).ToArray());
    }

    [TestMethod]
    public void QueueSchedule_ReplacesPreviousScheduleForLine()
    {
        var service = BlueLine.CreateService();
        service.QueueSchedule([BlueLine.Train("000", new TimeSpan(12, 0, 0)), BlueLine.Train("001", new TimeSpan(12, 3, 0))]);

        service.QueueSchedule([BlueLine.Train("000", new TimeSpan(13, 0, 0))]);

        Assert.HasCount(1, service.State.ScheduledTrains);
        Assert.AreEqual(new TimeSpan(13, 0, 0), service.State.DispatchQueue.Single().DepartureTime);
    }

    [TestMethod]
    public void QueueSchedule_RaisesScheduleAndDispatchQueueChanges()
    {
        var service = BlueLine.CreateService();
        var changes = new List<CtcStateChangeKind>();
        service.StateChanged += (_, e) => changes.Add(e.Kind);

        service.QueueSchedule([BlueLine.Train("000", new TimeSpan(12, 0, 0))]);

        CollectionAssert.AreEqual(new[] { CtcStateChangeKind.Schedule, CtcStateChangeKind.DispatchQueue }, changes);
    }

    [TestMethod]
    public void QueueSchedule_InvalidTrain_ChangesNothing()
    {
        var service = BlueLine.CreateService();
        var changes = new List<CtcStateChangeKind>();
        service.StateChanged += (_, e) => changes.Add(e.Kind);
        var unknownLine = BlueLine.Train("001", new TimeSpan(12, 3, 0));
        unknownLine.LineId = "RED";

        Assert.ThrowsExactly<ArgumentException>(() => service.QueueSchedule([BlueLine.Train("000", new TimeSpan(12, 0, 0)), unknownLine]));

        Assert.IsEmpty(service.State.ScheduledTrains);
        Assert.IsEmpty(service.State.DispatchQueue);
        Assert.IsEmpty(changes);
    }

    [TestMethod]
    public void QueueSchedule_BlockNotOnLine_Throws()
    {
        var service = BlueLine.CreateService();

        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => service.QueueSchedule([BlueLine.Train("000", new TimeSpan(12, 0, 0), route: ["A1", "X9"])]));

        Assert.Contains("uses a block that is not on Blue Line", ex.Message);
    }

    [TestMethod]
    public void QueueSchedule_UnconnectedConsecutiveBlocks_Throws()
    {
        var service = BlueLine.CreateService();

        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => service.QueueSchedule([BlueLine.Train("000", new TimeSpan(12, 0, 0), route: ["A1", "A2", "A4"])]));

        Assert.Contains("goes from A2 to A4, which are not connected", ex.Message);
    }

    [TestMethod]
    public void QueueSchedule_UntimedLastBlock_Throws()
    {
        var service = BlueLine.CreateService();

        Assert.ThrowsExactly<ArgumentException>(
            () => service.QueueSchedule([BlueLine.Train("000", new TimeSpan(12, 0, 0), route: ["A1", "A2", "A3"], timedBlocks: ["A1", "A2"])]));
    }

    [TestMethod]
    public void QueueSchedule_SingleBlockRoute_Throws()
    {
        var service = BlueLine.CreateService();

        Assert.ThrowsExactly<ArgumentException>(
            () => service.QueueSchedule([BlueLine.Train("000", new TimeSpan(12, 0, 0), route: ["A1"])]));
    }
}
