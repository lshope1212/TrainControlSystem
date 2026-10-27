using TrainController.Abstractions.Fleet;
using TrainController.Integration.Routing;

namespace TrainControl.Tests.TrainController;

[TestClass]
public class TrainFleetRoutingTests
{
    [TestMethod]
    [DataRow("TRAIN-001", ControllerType.Software)]
    [DataRow("TRAIN-002", ControllerType.Hardware)]
    [DataRow("TRAIN-003", ControllerType.Software)]
    [DataRow("TRAIN-004", ControllerType.Hardware)]
    [DataRow("TRAIN-005", ControllerType.Software)]
    [DataRow("TRAIN-006", ControllerType.Hardware)]
    [DataRow("TRAIN-007", ControllerType.Software)]
    [DataRow("TRAIN-008", ControllerType.Hardware)]
    [DataRow("TRAIN-009", ControllerType.Software)]
    [DataRow("TRAIN-010", ControllerType.Hardware)]
    public void Fleet_AssignsFixedControllerType(string trainId, ControllerType expected)
    {
        Assert.AreEqual(expected, TrainFleet.GetControllerType(trainId));
    }

    [TestMethod]
    [DataRow("TRAIN-001", ControllerType.Software)]
    [DataRow("TRAIN-002", ControllerType.Hardware)]
    [DataRow("TRAIN-003", ControllerType.Software)]
    [DataRow("TRAIN-004", ControllerType.Hardware)]
    [DataRow("TRAIN-005", ControllerType.Software)]
    [DataRow("TRAIN-006", ControllerType.Hardware)]
    [DataRow("TRAIN-007", ControllerType.Software)]
    [DataRow("TRAIN-008", ControllerType.Hardware)]
    [DataRow("TRAIN-009", ControllerType.Software)]
    [DataRow("TRAIN-010", ControllerType.Hardware)]
    public void Router_ResolvesTheFixedBackend(string trainId, ControllerType expected)
    {
        var software = new FakeTrainControllerBackend(ControllerType.Software);
        var hardware = new FakeTrainControllerBackend(ControllerType.Hardware);
        var router = new TrainControllerRouter(software, hardware);

        var backend = router.Resolve(trainId);

        Assert.AreSame(expected == ControllerType.Software ? software : hardware, backend);
        Assert.AreEqual(expected, backend.ControllerType);
    }

    [TestMethod]
    [DataRow("TRAIN-000")]
    [DataRow("TRAIN-011")]
    [DataRow("train-001")]
    [DataRow("T01")]
    [DataRow("TRAIN-1")]
    [DataRow(" TRAIN-001")]
    [DataRow("")]
    public void UnknownTrainId_IsRejectedExplicitly(string trainId)
    {
        var router = new TrainControllerRouter(
            new FakeTrainControllerBackend(ControllerType.Software),
            new FakeTrainControllerBackend(ControllerType.Hardware));

        Assert.IsFalse(TrainFleet.IsKnownTrain(trainId));
        Assert.ThrowsExactly<UnknownTrainException>(() => TrainFleet.GetControllerType(trainId));
        Assert.ThrowsExactly<UnknownTrainException>(() => router.Resolve(trainId));
    }

    [TestMethod]
    public void NullTrainId_IsRejectedExplicitly()
    {
        var router = new TrainControllerRouter(
            new FakeTrainControllerBackend(ControllerType.Software),
            new FakeTrainControllerBackend(ControllerType.Hardware));

        Assert.IsFalse(TrainFleet.IsKnownTrain(null));
        Assert.ThrowsExactly<UnknownTrainException>(() => router.Resolve(null));
    }

    [TestMethod]
    public void Fleet_HasTenTrainsInOrder_FiveSoftwareFiveHardware()
    {
        CollectionAssert.AreEqual(
            new[] { "TRAIN-001", "TRAIN-002", "TRAIN-003", "TRAIN-004", "TRAIN-005", "TRAIN-006", "TRAIN-007", "TRAIN-008", "TRAIN-009", "TRAIN-010" },
            TrainFleet.AllTrainIds.ToArray());
        CollectionAssert.AreEqual(
            new[] { "TRAIN-001", "TRAIN-003", "TRAIN-005", "TRAIN-007", "TRAIN-009" },
            TrainFleet.SoftwareTrainIds.ToArray());
        CollectionAssert.AreEqual(
            new[] { "TRAIN-002", "TRAIN-004", "TRAIN-006", "TRAIN-008", "TRAIN-010" },
            TrainFleet.HardwareTrainIds.ToArray());
    }

    [TestMethod]
    public void Router_RejectsBackendsOfTheWrongType()
    {
        var software = new FakeTrainControllerBackend(ControllerType.Software);
        var hardware = new FakeTrainControllerBackend(ControllerType.Hardware);

        Assert.ThrowsExactly<ArgumentException>(() => new TrainControllerRouter(hardware, software));
        Assert.ThrowsExactly<ArgumentException>(() => new TrainControllerRouter(software, software));
    }

    [TestMethod]
    public void Assignment_CannotBeChangedAtRuntime()
    {
        // The assignment API is read-only: no public writable members exist on TrainFleet.
        var writable = typeof(TrainFleet).GetProperties().Where(p => p.CanWrite).ToArray();
        Assert.IsEmpty(writable);
    }
}
