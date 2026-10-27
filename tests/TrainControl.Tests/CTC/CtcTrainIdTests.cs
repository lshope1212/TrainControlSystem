using CTC.Core.Models;
using CTC.Core.Scheduling;

namespace TrainControl.Tests.CTC;

[TestClass]
public class CtcTrainIdTests
{
    private static TimeSpan At(int hours, int minutes, int seconds) => new TimeSpan(hours, minutes, seconds);

    [TestMethod]
    [DataRow(0, "000")]
    [DataRow(1, "001")]
    [DataRow(9, "009")]
    [DataRow(10, "010")]
    [DataRow(99, "099")]
    [DataRow(100, "100")]
    [DataRow(999, "999")]
    public void Format_IsZeroBasedThreeDigits(int number, string expected)
    {
        Assert.AreEqual(expected, TrainIds.Format(number));
    }

    [TestMethod]
    public void Format_AboveMaximum_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => TrainIds.Format(1000));
    }

    [TestMethod]
    public void DisplayName_AddsPrefixToIdOnly()
    {
        Assert.AreEqual("Train 000", TrainIds.DisplayName("000"));
    }

    [TestMethod]
    public void NextAvailableNumber_EmptySystem_IsZero()
    {
        Assert.AreEqual(0, TrainIds.NextAvailableNumber(BlueLine.CreateService().State));
    }

    [TestMethod]
    public void NextAvailableNumber_FollowsQueuedTrains()
    {
        var service = BlueLine.CreateService();
        service.QueueSchedule([BlueLine.Train("000", At(12, 0, 0)), BlueLine.Train("001", At(12, 0, 10))]);

        Assert.AreEqual(2, TrainIds.NextAvailableNumber(service.State));
    }

    [TestMethod]
    public async Task NextAvailableNumber_CountsDispatchedTrains()
    {
        var service = BlueLine.CreateService();
        service.QueueSchedule([BlueLine.Train("004", At(12, 0, 0))]);
        await service.SetSystemTimeAsync(At(12, 0, 0));

        // Leave 004 known only as a dispatched train.
        service.State.ScheduledTrains.Clear();
        service.State.DispatchQueue.Clear();

        Assert.AreEqual("004", service.State.DispatchedTrains.Single().TrainId);
        Assert.AreEqual(5, TrainIds.NextAvailableNumber(service.State));
    }

    [TestMethod]
    public void Create_StartsAtFirstTrainNumber()
    {
        var service = BlueLine.CreateService();
        service.QueueSchedule([BlueLine.Train("000", At(12, 0, 0)), BlueLine.Train("001", At(12, 0, 10))]);

        var template = ScheduleTemplateFactory.Create(
            service.State.FindLine(BlueLine.LineId)!, 3, TrainIds.NextAvailableNumber(service.State));

        CollectionAssert.AreEqual(new[] { "002", "003", "004" }, template.TrainIds.ToArray());
    }

    [TestMethod]
    public void Create_PastMaximumId_FailsCleanly()
    {
        var line = BlueLine.CreateService().State.FindLine(BlueLine.LineId)!;

        CollectionAssert.AreEqual(new[] { "998", "999" }, ScheduleTemplateFactory.Create(line, 2, 998).TrainIds.ToArray());
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => ScheduleTemplateFactory.Create(line, 3, 998));
        Assert.Contains("999", ex.Message);
    }
}
