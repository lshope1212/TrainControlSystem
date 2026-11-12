using CTC.Core.Models;
using TrainControl.Common.Identity;
using TrainController.Abstractions.Fleet;

namespace TrainControl.Tests.Common;

[TestClass]
public class TrainIdConventionTests
{
    [TestMethod]
    [DataRow("000")]
    [DataRow("007")]
    [DataRow("999")]
    public void CanonicalIds_AreThreeDigits(string id) => Assert.IsTrue(TrainIdConvention.IsCanonical(id));

    [TestMethod]
    [DataRow("01")]
    [DataRow("0001")]
    [DataRow("TRAIN-001")]
    [DataRow("A12")]
    [DataRow("")]
    [DataRow(null)]
    public void OtherLabels_AreNotCanonical(string? id) => Assert.IsFalse(TrainIdConvention.IsCanonical(id));

    [TestMethod]
    [DataRow("TRAIN-001", "001")]
    [DataRow("train-010", "010")]
    [DataRow("Train 007", "007")]
    [DataRow(" 123 ", "123")]
    public void ModuleLabels_TranslateToTheCanonicalId(string label, string expected)
    {
        Assert.IsTrue(TrainIdConvention.TryNormalize(label, out var canonical));
        Assert.AreEqual(expected, canonical);
    }

    [TestMethod]
    [DataRow("01")]
    [DataRow("TRAIN-1")]
    [DataRow("TRAIN-0001")]
    [DataRow("Locomotive 5")]
    public void AmbiguousLabels_AreRejectedNotGuessed(string label) => Assert.IsFalse(TrainIdConvention.TryNormalize(label, out _));

    [TestMethod]
    public void TrainControllerLabel_KeepsTheNumber() => Assert.AreEqual("TRAIN-042", TrainIdConvention.ToTrainControllerLabel("042"));

    [TestMethod]
    public void CtcFormatsCanonicalIds()
    {
        Assert.AreEqual("010", TrainIds.Format(10));
        Assert.IsTrue(TrainIdConvention.IsCanonical(TrainIds.Format(0)));
        Assert.IsTrue(TrainIdConvention.TryNormalize(TrainIds.DisplayName("005"), out var id));
        Assert.AreEqual("005", id);
    }

    // ---------------- Dispatchable fleet ----------------

    [TestMethod]
    [DataRow("001")]
    [DataRow("005")]
    [DataRow("010")]
    public void FleetIds_AreDispatchable(string id) => Assert.IsTrue(TrainIdConvention.IsDispatchable(id));

    [TestMethod]
    [DataRow("000")]
    [DataRow("011")]
    [DataRow("999")]
    [DataRow("01")]
    [DataRow("TRAIN-001")]
    [DataRow("Train 001")]
    [DataRow("")]
    [DataRow(null)]
    public void OtherIds_AreNotDispatchable(string? id) => Assert.IsFalse(TrainIdConvention.IsDispatchable(id));

    [TestMethod]
    public void CanonicalAndDispatchable_AreDistinct()
    {
        // "000" and "011" stay valid canonical IDs for general-purpose normalization; they just cannot be dispatched.
        Assert.IsTrue(TrainIdConvention.IsCanonical("000") && !TrainIdConvention.IsDispatchable("000"));
        Assert.IsTrue(TrainIdConvention.TryNormalize("TRAIN-011", out var canonical));
        Assert.AreEqual("011", canonical);
        Assert.IsFalse(TrainIdConvention.IsDispatchable(canonical));
    }

    /// <summary>The shared dispatchable fleet is exactly the Train Controller's fleet, label for label.</summary>
    [TestMethod]
    public void DispatchableFleet_MatchesTheTrainControllerFleet()
    {
        Assert.AreEqual(TrainFleet.TrainCount, TrainIdConvention.DispatchableTrainCount);
        CollectionAssert.AreEqual(
            TrainFleet.AllTrainIds.ToArray(),
            TrainIdConvention.DispatchableTrainIds.Select(TrainIdConvention.ToTrainControllerLabel).ToArray());
        Assert.IsFalse(TrainFleet.IsKnownTrain(TrainIdConvention.ToTrainControllerLabel("000")), "There is no TRAIN-000.");
        Assert.IsFalse(TrainFleet.IsKnownTrain(TrainIdConvention.ToTrainControllerLabel("011")), "The fleet is not expanded.");
    }

    /// <summary>CTC Train 001 -> canonical "001" -> Train Controller "TRAIN-001" -> canonical "001", for every fleet train.</summary>
    [TestMethod]
    public void EveryFleetId_RoundTripsThroughEveryModuleLabel()
    {
        foreach (var id in TrainIdConvention.DispatchableTrainIds)
        {
            Assert.IsTrue(TrainIdConvention.TryNormalize(TrainIds.DisplayName(id), out var fromCtc));
            Assert.AreEqual(id, fromCtc);

            string controllerLabel = TrainIdConvention.ToTrainControllerLabel(id);
            Assert.IsTrue(TrainFleet.IsKnownTrain(controllerLabel), controllerLabel);

            Assert.IsTrue(TrainIdConvention.TryNormalize(controllerLabel, out var fromController));
            Assert.AreEqual(id, fromController);
        }

        Assert.AreEqual("TRAIN-001", TrainIdConvention.ToTrainControllerLabel("001"));
    }

    /// <summary>The Software/Hardware assignment is unchanged: odd trains Software, even trains Hardware.</summary>
    [TestMethod]
    [DataRow("001", ControllerType.Software)]
    [DataRow("002", ControllerType.Hardware)]
    [DataRow("003", ControllerType.Software)]
    [DataRow("004", ControllerType.Hardware)]
    [DataRow("005", ControllerType.Software)]
    [DataRow("006", ControllerType.Hardware)]
    [DataRow("007", ControllerType.Software)]
    [DataRow("008", ControllerType.Hardware)]
    [DataRow("009", ControllerType.Software)]
    [DataRow("010", ControllerType.Hardware)]
    public void ControllerAssignment_IsUnchanged(string id, ControllerType expected) =>
        Assert.AreEqual(expected, TrainFleet.GetControllerType(TrainIdConvention.ToTrainControllerLabel(id)));
}
