using CTC.Core.Models;
using CTC.Core.Scheduling;
using CTC.Core.Services;
using TrainControl.Contracts.Messages;

namespace TrainControl.Tests.CTC;

[TestClass]
public class CtcScheduleTests
{
    // Blocks deliberately out of number order to check the start block is not just "first row".
    private static CTCService CreateService()
    {
        var service = new CTCService(new FakeMessageSender());
        service.ApplyTrackLayout(new TrackLayoutMessage
        {
            Lines =
            {
                new TrackLineDefinition
                {
                    LineId = "BLUE",
                    Name = "Blue Line",
                    Blocks =
                    {
                        new TrackBlockDefinition { BlockId = "B2", BlockNumber = 2, Section = "A", HasCrossing = true },
                        new TrackBlockDefinition { BlockId = "B1", BlockNumber = 1, Section = "A" },
                        new TrackBlockDefinition { BlockId = "B10", BlockNumber = 10, Section = "B", StationName = "Station B", HasSignal = true },
                        new TrackBlockDefinition { BlockId = "B15", BlockNumber = 15, Section = "C", StationName = "Station C" },
                    },
                },
            },
        });
        return service;
    }

    private static ScheduleTemplate CreateTemplate(CTCService service, int trainCount) =>
        ScheduleTemplateFactory.Create(service.State.FindLine("BLUE")!, trainCount);

    private static ScheduleTemplateRow Row(ScheduleTemplate template, string blockId) =>
        template.Rows.Single(row => row.BlockId == blockId);

    private static ScheduledTrain Train(string trainId, TimeSpan departure) => new ScheduledTrain
    {
        TrainId = trainId,
        LineId = "BLUE",
        StartBlockId = "B1",
        DepartureTime = departure,
    };

    [TestMethod]
    public void Create_ThreeTrains_ProducesThreeTrainColumns()
    {
        var template = CreateTemplate(CreateService(), 3);

        CollectionAssert.AreEqual(new[] { "Train 1", "Train 2", "Train 3" }, template.TrainIds.ToArray());
        Assert.HasCount(4, template.Rows);
        Assert.IsTrue(template.Rows.All(row => row.TrainTimes.Length == 3 && row.TrainTimes.All(string.IsNullOrEmpty)));
    }

    [TestMethod]
    public void Create_MarksLowestBlockAsStartAndOnlyStartAndStationsEditable()
    {
        var template = CreateTemplate(CreateService(), 1);

        Assert.IsTrue(Row(template, "B1").IsRouteStart);
        Assert.IsFalse(Row(template, "B2").IsTimeEditable);
        Assert.IsTrue(Row(template, "B10").IsTimeEditable);
        Assert.AreEqual("Station B; Signal", Row(template, "B10").Infrastructure);
        Assert.AreEqual("Railway Crossing", Row(template, "B2").Infrastructure);
    }

    [TestMethod]
    public void Convert_StartBlockTime_BecomesDepartureTime()
    {
        var template = CreateTemplate(CreateService(), 1);
        Row(template, "B1").TrainTimes[0] = "12:00:00";
        Row(template, "B10").TrainTimes[0] = "12:08:00";

        var result = ScheduleTemplateConverter.Convert(template);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        var train = result.Trains.Single();
        Assert.AreEqual("Train 1", train.TrainId);
        Assert.AreEqual("BLUE", train.LineId);
        Assert.AreEqual("B1", train.StartBlockId);
        Assert.AreEqual(new TimeSpan(12, 0, 0), train.DepartureTime);
    }

    [TestMethod]
    public void Convert_PopulatedStationCell_BecomesStop()
    {
        var template = CreateTemplate(CreateService(), 1);
        Row(template, "B1").TrainTimes[0] = "12:00:00";
        Row(template, "B10").TrainTimes[0] = "12:08:00";

        var stop = ScheduleTemplateConverter.Convert(template).Trains.Single().Stops.Single();

        Assert.AreEqual("B10", stop.BlockId);
        Assert.AreEqual("Station B", stop.StationName);
        Assert.AreEqual(new TimeSpan(12, 8, 0), stop.ArrivalTime);
    }

    [TestMethod]
    public void Convert_BlankStationCell_CreatesNoStop()
    {
        var template = CreateTemplate(CreateService(), 2);
        Row(template, "B1").TrainTimes[0] = "12:00:00";
        Row(template, "B1").TrainTimes[1] = "12:03:00";
        Row(template, "B10").TrainTimes[0] = "12:08:00";
        Row(template, "B15").TrainTimes[1] = "12:14:00";

        var result = ScheduleTemplateConverter.Convert(template);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.AreEqual("B10", result.Trains[0].Stops.Single().BlockId);
        Assert.AreEqual("B15", result.Trains[1].Stops.Single().BlockId);
    }

    [TestMethod]
    public void Convert_MissingStartTime_Fails()
    {
        var template = CreateTemplate(CreateService(), 2);
        Row(template, "B1").TrainTimes[0] = "12:00:00";
        Row(template, "B10").TrainTimes[0] = "12:08:00";
        Row(template, "B10").TrainTimes[1] = "12:11:00";

        var result = ScheduleTemplateConverter.Convert(template);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("Train 2 is missing a route start time.", result.ErrorMessage);
        Assert.IsEmpty(result.Trains);
    }

    [TestMethod]
    public void Convert_InvalidTimeText_Fails()
    {
        var template = CreateTemplate(CreateService(), 1);
        Row(template, "B1").TrainTimes[0] = "12:00:00";
        Row(template, "B10").TrainTimes[0] = "12:8";

        var result = ScheduleTemplateConverter.Convert(template);

        Assert.IsFalse(result.IsSuccess);
        Assert.Contains("invalid arrival time at Station B", result.ErrorMessage!);
    }

    [TestMethod]
    public void Convert_ArrivalBeforeDeparture_Fails()
    {
        var template = CreateTemplate(CreateService(), 1);
        Row(template, "B1").TrainTimes[0] = "12:00:00";
        Row(template, "B10").TrainTimes[0] = "11:59:00";

        var result = ScheduleTemplateConverter.Convert(template);

        Assert.IsFalse(result.IsSuccess);
        Assert.Contains("earlier than its departure time", result.ErrorMessage!);
    }

    [TestMethod]
    public void Convert_NoStationStops_Fails()
    {
        var template = CreateTemplate(CreateService(), 1);
        Row(template, "B1").TrainTimes[0] = "12:00:00";

        var result = ScheduleTemplateConverter.Convert(template);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("Train 1 has no scheduled station stops.", result.ErrorMessage);
    }

    [TestMethod]
    public void QueueSchedule_CreatesOneQueueEntryPerTrain()
    {
        var service = CreateService();

        service.QueueSchedule([Train("Train 1", new TimeSpan(12, 0, 0)), Train("Train 2", new TimeSpan(12, 3, 0))]);

        Assert.HasCount(2, service.State.ScheduledTrains);
        Assert.HasCount(2, service.State.DispatchQueue);
        Assert.IsTrue(service.State.DispatchQueue.All(entry => entry.LineId == "BLUE" && entry.QueueStatus == DispatchQueueStatus.Queued));
    }

    [TestMethod]
    public void QueueSchedule_SortsQueueByDepartureTime()
    {
        var service = CreateService();

        service.QueueSchedule(
        [
            Train("Train 1", new TimeSpan(12, 6, 0)),
            Train("Train 2", new TimeSpan(12, 0, 0)),
            Train("Train 3", new TimeSpan(12, 3, 0)),
        ]);

        CollectionAssert.AreEqual(
            new[] { "Train 2", "Train 3", "Train 1" },
            service.State.DispatchQueue.Select(entry => entry.TrainId).ToArray());
    }

    [TestMethod]
    public void QueueSchedule_ReplacesPreviousScheduleForLine()
    {
        var service = CreateService();
        service.QueueSchedule([Train("Train 1", new TimeSpan(12, 0, 0)), Train("Train 2", new TimeSpan(12, 3, 0))]);

        service.QueueSchedule([Train("Train 1", new TimeSpan(13, 0, 0))]);

        Assert.HasCount(1, service.State.ScheduledTrains);
        Assert.AreEqual(new TimeSpan(13, 0, 0), service.State.DispatchQueue.Single().DepartureTime);
    }

    [TestMethod]
    public void QueueSchedule_RaisesScheduleAndDispatchQueueChanges()
    {
        var service = CreateService();
        var changes = new List<CtcStateChangeKind>();
        service.StateChanged += (_, e) => changes.Add(e.Kind);

        service.QueueSchedule([Train("Train 1", new TimeSpan(12, 0, 0))]);

        CollectionAssert.AreEqual(new[] { CtcStateChangeKind.Schedule, CtcStateChangeKind.DispatchQueue }, changes);
    }

    [TestMethod]
    public void QueueSchedule_InvalidTrain_ChangesNothing()
    {
        var service = CreateService();
        var changes = new List<CtcStateChangeKind>();
        service.StateChanged += (_, e) => changes.Add(e.Kind);
        var unknownLine = Train("Train 2", new TimeSpan(12, 3, 0));
        unknownLine.LineId = "RED";

        Assert.ThrowsExactly<ArgumentException>(() => service.QueueSchedule([Train("Train 1", new TimeSpan(12, 0, 0)), unknownLine]));

        Assert.IsEmpty(service.State.ScheduledTrains);
        Assert.IsEmpty(service.State.DispatchQueue);
        Assert.IsEmpty(changes);
    }
}
