using CTC.Core.Models;
using TrainControl.Common.Identity;

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
}
