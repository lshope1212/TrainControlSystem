using CTC.Core.Models;
using CTC.Core.Scheduling;
using TrainControl.Common.Identity;

namespace TrainControl.Tests.CTC;

[TestClass]
public class CtcTrainIdTests
{
    private static TimeSpan At(int hours, int minutes, int seconds) => new TimeSpan(hours, minutes, seconds);

    private static readonly string[] Fleet = ["001", "002", "003", "004", "005", "006", "007", "008", "009", "010"];

    /// <summary>Queues Blue trains with the given IDs, departing 10 s apart from noon.</summary>
    private static void QueueBlue(global::CTC.Core.Services.CTCService service, params string[] trainIds) =>
        service.QueueSchedule(trainIds.Select((id, i) => BlueLine.Train(id, At(12, 0, 0) + TimeSpan.FromSeconds(10 * i))));

    // ---------------- Formatting (general-purpose, unchanged) ----------------

    [TestMethod]
    [DataRow(0, "000")]
    [DataRow(1, "001")]
    [DataRow(9, "009")]
    [DataRow(10, "010")]
    [DataRow(99, "099")]
    [DataRow(100, "100")]
    [DataRow(999, "999")]
    public void Format_IsThreeDigits(int number, string expected)
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
        Assert.AreEqual("Train 001", TrainIds.DisplayName("001"));
    }

    // ---------------- Supported fleet ----------------

    [TestMethod]
    public void SupportedIds_AreExactlyTheTenFleetTrains()
    {
        Assert.AreEqual(1, TrainIds.FirstNumber);
        Assert.AreEqual(10, TrainIds.LastNumber);
        CollectionAssert.AreEqual(Fleet, TrainIdConvention.DispatchableTrainIds.ToArray());
        Assert.IsTrue(Fleet.All(TrainIds.IsSupported));
    }

    [TestMethod]
    [DataRow("000")]
    [DataRow("011")]
    [DataRow("999")]
    [DataRow("01")]
    [DataRow("1")]
    [DataRow("TRAIN-001")]
    [DataRow("Train 001")]
    [DataRow("")]
    [DataRow(null)]
    public void UnsupportedIds_AreNotSupported(string? id) => Assert.IsFalse(TrainIds.IsSupported(id));

    // ---------------- Allocation ----------------

    [TestMethod]
    public void Allocate_EmptySystem_StartsAt001()
    {
        CollectionAssert.AreEqual(new[] { "001" }, TrainIds.Allocate(BlueLine.CreateService().State, 1).ToArray());
    }

    [TestMethod]
    public void Allocate_SeveralTrains_AreSequential()
    {
        CollectionAssert.AreEqual(new[] { "001", "002", "003" }, TrainIds.Allocate(BlueLine.CreateService().State, 3).ToArray());
    }

    [TestMethod]
    public void Allocate_FollowsQueuedTrains()
    {
        var service = BlueLine.CreateService();
        QueueBlue(service, "001", "002");

        CollectionAssert.AreEqual(new[] { "003", "004" }, TrainIds.Allocate(service.State, 2).ToArray());
    }

    [TestMethod]
    public async Task Allocate_CountsDispatchedTrains()
    {
        var service = BlueLine.CreateService();
        service.QueueSchedule([BlueLine.Train("001", At(12, 0, 0))]);
        await service.SetSystemTimeAsync(At(12, 0, 0));

        // Leave 001 known only as a dispatched train.
        service.State.ScheduledTrains.Clear();
        service.State.DispatchQueue.Clear();

        Assert.AreEqual("001", service.State.DispatchedTrains.Single().TrainId);
        CollectionAssert.AreEqual(new[] { "002" }, TrainIds.Allocate(service.State, 1).ToArray());
    }

    [TestMethod]
    public void Allocate_CountsQueueEntriesOnTheirOwn()
    {
        var service = BlueLine.CreateService();
        QueueBlue(service, "001");
        service.State.ScheduledTrains.Clear();

        Assert.AreEqual("001", service.State.DispatchQueue.Single().TrainId);
        CollectionAssert.AreEqual(new[] { "002" }, TrainIds.Allocate(service.State, 1).ToArray());
    }

    [TestMethod]
    public void Allocate_IdsStayUniqueWhenSwitchingLines()
    {
        var service = BlueLine.CreateTwoLineService();
        QueueBlue(service, TrainIds.Allocate(service.State, 2).ToArray());

        // The dispatcher switches to the Green Line: its first train must not be 001 again.
        var greenIds = TrainIds.Allocate(service.State, 2);
        CollectionAssert.AreEqual(new[] { "003", "004" }, greenIds.ToArray());
        service.QueueSchedule(greenIds.Select((id, i) => BlueLine.GreenTrain(id, At(12, 5, 10 * i))));

        CollectionAssert.AreEqual(new[] { "005" }, TrainIds.Allocate(service.State, 1).ToArray());
        Assert.HasCount(4, service.State.ScheduledTrains.Select(train => train.TrainId).Distinct());
    }

    [TestMethod]
    public void Allocate_AllTenTrains_Succeeds_AndAnEleventhFails()
    {
        var service = BlueLine.CreateTwoLineService();
        var blue = TrainIds.Allocate(service.State, 6);
        QueueBlue(service, blue.ToArray());
        var green = TrainIds.Allocate(service.State, 4);
        service.QueueSchedule(green.Select((id, i) => BlueLine.GreenTrain(id, At(12, 5, 10 * i))));

        CollectionAssert.AreEqual(Fleet, blue.Concat(green).ToArray());
        Assert.IsEmpty(TrainIds.AvailableIds(service.State));

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => TrainIds.Allocate(service.State, 1));
        Assert.AreEqual(TrainIds.NotEnoughIdsMessage(1, 0), ex.Message);
        StringAssert.Contains(ex.Message, "Train 001 to Train 010");
    }

    [TestMethod]
    public void Allocate_MoreThanRemaining_FailsWithCount()
    {
        var service = BlueLine.CreateService();
        QueueBlue(service, "001", "002", "003", "004", "005", "006", "007", "008");

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => TrainIds.Allocate(service.State, 3));
        Assert.AreEqual(TrainIds.NotEnoughIdsMessage(3, 2), ex.Message);
        CollectionAssert.AreEqual(new[] { "009", "010" }, TrainIds.Allocate(service.State, 2).ToArray());
    }

    [TestMethod]
    public void Allocate_ReusesOnlyIdsNoLongerKnown()
    {
        // A replaced schedule releases its IDs; IDs still in use elsewhere are skipped.
        var service = BlueLine.CreateTwoLineService();
        QueueBlue(service, "001", "002");
        service.QueueSchedule([BlueLine.GreenTrain("003", At(12, 5, 0))]);
        QueueBlue(service, "004");

        CollectionAssert.AreEqual(new[] { "001", "002", "005" }, TrainIds.Allocate(service.State, 3).ToArray());
    }

    [TestMethod]
    public void Allocate_ExistingIdsAreNotRenumbered()
    {
        var service = BlueLine.CreateService();
        QueueBlue(service, "004", "007");

        TrainIds.Allocate(service.State, 3);

        CollectionAssert.AreEqual(new[] { "004", "007" }, service.State.ScheduledTrains.Select(train => train.TrainId).ToArray());
        CollectionAssert.AreEqual(new[] { "001", "002", "003" }, TrainIds.Allocate(service.State, 3).ToArray());
    }

    // ---------------- Template factory ----------------

    [TestMethod]
    public void Create_DefaultsToTrain001()
    {
        var line = BlueLine.CreateService().State.FindLine(BlueLine.LineId)!;

        CollectionAssert.AreEqual(new[] { "001", "002" }, ScheduleTemplateFactory.Create(line, 2).TrainIds.ToArray());
    }

    [TestMethod]
    public void Create_WithAllocatedIds_UsesThem()
    {
        var service = BlueLine.CreateService();
        QueueBlue(service, "001", "002");

        var template = ScheduleTemplateFactory.Create(service.State.FindLine(BlueLine.LineId)!, TrainIds.Allocate(service.State, 3));

        CollectionAssert.AreEqual(new[] { "003", "004", "005" }, template.TrainIds.ToArray());
    }

    [TestMethod]
    public void Create_PastLastSupportedId_FailsCleanly()
    {
        var line = BlueLine.CreateService().State.FindLine(BlueLine.LineId)!;

        CollectionAssert.AreEqual(new[] { "009", "010" }, ScheduleTemplateFactory.Create(line, 2, 9).TrainIds.ToArray());
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => ScheduleTemplateFactory.Create(line, 3, 9));
        Assert.Contains("010", ex.Message);
        Assert.ThrowsExactly<InvalidOperationException>(() => ScheduleTemplateFactory.Create(line, 1, 11));
        Assert.ThrowsExactly<InvalidOperationException>(() => ScheduleTemplateFactory.Create(line, 11));
    }

    [TestMethod]
    public void Create_FromTrain000_IsRejected()
    {
        var line = BlueLine.CreateService().State.FindLine(BlueLine.LineId)!;

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ScheduleTemplateFactory.Create(line, 1, 0));
    }

    [TestMethod]
    [DataRow("000")]
    [DataRow("011")]
    [DataRow("01")]
    public void Create_WithUnsupportedId_IsRejected(string id)
    {
        var line = BlueLine.CreateService().State.FindLine(BlueLine.LineId)!;

        var ex = Assert.ThrowsExactly<ArgumentException>(() => ScheduleTemplateFactory.Create(line, ["001", id]));
        Assert.AreEqual(TrainIds.UnsupportedMessage(id), ex.Message);
    }

    [TestMethod]
    public void Create_WithRepeatedId_IsRejected()
    {
        var line = BlueLine.CreateService().State.FindLine(BlueLine.LineId)!;

        Assert.ThrowsExactly<ArgumentException>(() => ScheduleTemplateFactory.Create(line, ["001", "001"]));
    }

    // ---------------- QueueSchedule (the direct scheduling API) ----------------

    [TestMethod]
    [DataRow("000")]
    [DataRow("011")]
    [DataRow("100")]
    [DataRow("01")]
    [DataRow("TRAIN-001")]
    public void QueueSchedule_UnsupportedId_IsRejected_AndNothingIsQueued(string id)
    {
        var service = BlueLine.CreateService();

        var ex = Assert.ThrowsExactly<ArgumentException>(() =>
            service.QueueSchedule([BlueLine.Train("001", At(12, 0, 0)), BlueLine.Train(id, At(12, 0, 10))]));

        Assert.AreEqual(TrainIds.UnsupportedMessage(id), ex.Message);
        Assert.IsEmpty(service.State.ScheduledTrains);
        Assert.IsEmpty(service.State.DispatchQueue);
    }

    [TestMethod]
    public void QueueSchedule_AllTenSupportedIds_AreAccepted()
    {
        var service = BlueLine.CreateService();

        QueueBlue(service, Fleet);

        CollectionAssert.AreEqual(Fleet, service.State.DispatchQueue.Select(entry => entry.TrainId).ToArray());
    }

    [TestMethod]
    public void QueueSchedule_IdScheduledOnAnotherLine_IsRejected()
    {
        var service = BlueLine.CreateTwoLineService();
        QueueBlue(service, "001");

        var ex = Assert.ThrowsExactly<ArgumentException>(() => service.QueueSchedule([BlueLine.GreenTrain("001", At(12, 5, 0))]));

        Assert.AreEqual("Train 001 is already scheduled, queued or dispatched on Blue Line. Train IDs must be unique across all lines.", ex.Message);
        Assert.AreEqual(BlueLine.LineId, service.State.ScheduledTrains.Single().LineId);
        Assert.AreEqual(BlueLine.LineId, service.State.DispatchQueue.Single().LineId);
    }

    [TestMethod]
    public async Task QueueSchedule_IdDispatchedOnAnotherLine_IsRejected()
    {
        var service = BlueLine.CreateTwoLineService();
        QueueBlue(service, "001");
        await service.SetSystemTimeAsync(At(12, 0, 0));
        Assert.AreEqual("001", service.State.DispatchedTrains.Single().TrainId);

        Assert.ThrowsExactly<ArgumentException>(() => service.QueueSchedule([BlueLine.GreenTrain("001", At(12, 5, 0))]));
        // Replacing both lines at once does not free a train that is already dispatched.
        Assert.ThrowsExactly<ArgumentException>(() =>
            service.QueueSchedule([BlueLine.Train("002", At(12, 5, 0)), BlueLine.GreenTrain("001", At(12, 5, 10))]));
    }

    [TestMethod]
    public void QueueSchedule_ReplacingASchedule_FreesItsIdsForTheOtherLine()
    {
        // Moving a not-yet-dispatched train's ID from Blue to Green in one schedule is not a duplicate:
        // Blue's old schedule is replaced by the same call.
        var service = BlueLine.CreateTwoLineService();
        QueueBlue(service, "001");

        service.QueueSchedule([BlueLine.Train("002", At(12, 0, 0)), BlueLine.GreenTrain("001", At(12, 5, 0))]);

        Assert.AreEqual(BlueLine.GreenLineId, service.State.ScheduledTrains.Single(train => train.TrainId == "001").LineId);
        Assert.AreEqual(BlueLine.LineId, service.State.ScheduledTrains.Single(train => train.TrainId == "002").LineId);
    }

    [TestMethod]
    public void QueueSchedule_SameLineRequeue_KeepsExistingIds()
    {
        // Re-queueing a line's own trains is not a cross-line duplicate.
        var service = BlueLine.CreateService();
        QueueBlue(service, "001", "002");

        QueueBlue(service, "001", "002");

        CollectionAssert.AreEqual(new[] { "001", "002" }, service.State.ScheduledTrains.Select(train => train.TrainId).ToArray());
    }

    [TestMethod]
    public void GeneratedTemplate_QueuesWithItsAllocatedIds()
    {
        var service = BlueLine.CreateService();
        var template = ScheduleTemplateFactory.Create(service.State.FindLine(BlueLine.LineId)!, TrainIds.Allocate(service.State, 1));
        foreach (var row in template.Rows.Where(row => row.BlockId is "A1" or "B10"))
        {
            row.SetTrainTime(0, row.BlockId == "A1" ? "12:00:00" : "12:01:00");
        }

        var conversion = ScheduleTemplateConverter.Convert(template);
        Assert.IsTrue(conversion.IsSuccess, conversion.ErrorMessage);
        service.QueueSchedule(conversion.Trains);

        Assert.AreEqual("001", service.State.DispatchQueue.Single().TrainId);
        Assert.AreEqual("Train 001", TrainIds.DisplayName(service.State.DispatchQueue.Single().TrainId));
    }
}
