using CTC.TestUI.Wpf.SampleData;
using TrainControl.Contracts.Messages;

namespace CTC.Wpf.Tests;

/// <summary>
/// The TestUI's "Send Sample Layout" fixture, generated from the course workbook (vF5).
/// </summary>
[TestClass]
public class SampleTrackLayoutTests
{
    private static readonly TrackLayoutMessage Layout = SampleTrackLayout.Create();

    private static TrackLineDefinition Line(string lineId) => Layout.Lines.Single(line => line.LineId == lineId);

    private static TrackBlockDefinition Block(string lineId, int number) => Line(lineId).Blocks.Single(block => block.BlockNumber == number);

    private static int[] Numbers(string lineId, Func<TrackBlockDefinition, bool> predicate) =>
        Line(lineId).Blocks.Where(predicate).Select(block => block.BlockNumber).Order().ToArray();

    [TestMethod]
    public void ContainsBlueRedAndGreenLines()
    {
        Assert.HasCount(3, Layout.Lines);
        CollectionAssert.AreEquivalent(new[] { "Blue Line", "Red Line", "Green Line" }, Layout.Lines.Select(line => line.Name).ToArray());

        Assert.HasCount(15, Line("BLUE").Blocks);
        Assert.HasCount(76, Line("RED").Blocks);
        Assert.HasCount(150, Line("GREEN").Blocks);
        Assert.AreEqual(241, Layout.Lines.Sum(line => line.Blocks.Count));
    }

    [TestMethod]
    public void BlockIds_AreUniqueAcrossTheWholeLayout()
    {
        var ids = Layout.Lines.SelectMany(line => line.Blocks).Select(block => block.BlockId).ToList();

        Assert.HasCount(ids.Count, ids.Distinct());
    }

    [TestMethod]
    public void BlueLine_KeepsOriginalTestUiBlockIds()
    {
        CollectionAssert.AreEqual(
            new[] { "A1", "A2", "A3", "A4", "A5", "B6", "B7", "B8", "B9", "B10", "C11", "C12", "C13", "C14", "C15" },
            Line("BLUE").Blocks.Select(block => block.BlockId).ToArray());
    }

    [TestMethod]
    public void RedAndGreenBlockIds_AreLineQualified()
    {
        Assert.AreEqual("RED-C7", Block("RED", 7).BlockId);
        Assert.AreEqual("GREEN-W132", Block("GREEN", 132).BlockId);
    }

    [TestMethod]
    public void EveryConnection_ResolvesWithinItsLine_AndIsSymmetric()
    {
        foreach (var line in Layout.Lines)
        {
            var byId = line.Blocks.ToDictionary(block => block.BlockId);
            foreach (var block in line.Blocks)
            {
                Assert.IsNotEmpty(block.ConnectedBlockIds, $"{block.BlockId} has no connections.");
                foreach (var otherId in block.ConnectedBlockIds)
                {
                    Assert.IsTrue(byId.ContainsKey(otherId), $"{block.BlockId} -> {otherId} is not a {line.Name} block.");
                    Assert.Contains(block.BlockId, byId[otherId].ConnectedBlockIds, $"{otherId} does not connect back to {block.BlockId}.");
                }
            }
        }
    }

    [TestMethod]
    public void BlueLine_InfrastructureMatchesWorkbook()
    {
        CollectionAssert.AreEqual(new[] { 3 }, Numbers("BLUE", block => block.HasCrossing));
        CollectionAssert.AreEqual(new[] { 5 }, Numbers("BLUE", block => block.HasSwitch));
        CollectionAssert.AreEqual(new[] { 6, 11 }, Numbers("BLUE", block => block.HasSignal));
        Assert.AreEqual("Station B", Block("BLUE", 10).StationName);
        Assert.AreEqual("Station C", Block("BLUE", 15).StationName);
        CollectionAssert.AreEquivalent(new[] { "A4", "B6", "C11" }, Block("BLUE", 5).ConnectedBlockIds);
    }

    [TestMethod]
    public void RedLine_InfrastructureMatchesWorkbook()
    {
        CollectionAssert.AreEqual(new[] { 9, 15, 27, 32, 38, 43, 52 }, Numbers("RED", block => block.HasSwitch));
        CollectionAssert.AreEqual(new[] { 11, 47 }, Numbers("RED", block => block.HasCrossing));
        CollectionAssert.AreEqual(new[] { 7, 16, 21, 25, 35, 45, 48, 60 }, Numbers("RED", block => block.StationName != string.Empty));
        Assert.AreEqual("Shadyside", Block("RED", 7).StationName);
        Assert.AreEqual("South Hills Junction", Block("RED", 60).StationName);
        Assert.AreEqual(43.2, Block("RED", 52).LengthMeters);
        Assert.AreEqual(70, Block("RED", 30).SpeedLimitKilometersPerHour);
    }

    [TestMethod]
    public void RedLine_TopologyFollowsSwitchLegs()
    {
        CollectionAssert.AreEquivalent(new[] { "RED-A2", "RED-F16" }, Block("RED", 1).ConnectedBlockIds);
        CollectionAssert.AreEquivalent(new[] { "RED-E15", "RED-F17", "RED-A1" }, Block("RED", 16).ConnectedBlockIds);
        CollectionAssert.AreEquivalent(new[] { "RED-H26", "RED-H28", "RED-T76" }, Block("RED", 27).ConnectedBlockIds);
        CollectionAssert.AreEquivalent(new[] { "RED-H32", "RED-H34", "RED-R72" }, Block("RED", 33).ConnectedBlockIds);
        CollectionAssert.AreEquivalent(new[] { "RED-H37", "RED-H39", "RED-Q71" }, Block("RED", 38).ConnectedBlockIds);
        CollectionAssert.AreEquivalent(new[] { "RED-H43", "RED-H45", "RED-O67" }, Block("RED", 44).ConnectedBlockIds);
        CollectionAssert.AreEquivalent(new[] { "RED-J51", "RED-J53", "RED-N66" }, Block("RED", 52).ConnectedBlockIds);

        // N66 returns to J52 and Q71 rejoins H38: neither continues to the next block number.
        CollectionAssert.DoesNotContain(Block("RED", 66).ConnectedBlockIds.ToList(), "RED-O67");
        CollectionAssert.DoesNotContain(Block("RED", 71).ConnectedBlockIds.ToList(), "RED-R72");
    }

    [TestMethod]
    public void GreenLine_InfrastructureMatchesWorkbook()
    {
        CollectionAssert.AreEqual(new[] { 12, 28, 58, 62, 76, 85 }, Numbers("GREEN", block => block.HasSwitch));
        CollectionAssert.AreEqual(new[] { 19, 108 }, Numbers("GREEN", block => block.HasCrossing));
        CollectionAssert.AreEqual(
            new[] { 2, 9, 16, 22, 31, 39, 48, 57, 65, 73, 77, 88, 96, 105, 114, 123, 132, 141 },
            Numbers("GREEN", block => block.StationName != string.Empty));
        Assert.AreEqual("Pioneer", Block("GREEN", 2).StationName);
        Assert.AreEqual("Castle Shannon", Block("GREEN", 96).StationName);
        Assert.AreEqual("Central", Block("GREEN", 141).StationName);
    }

    [TestMethod]
    public void GreenLine_TopologyFollowsSwitchLegs()
    {
        CollectionAssert.AreEquivalent(new[] { "GREEN-A2", "GREEN-D13" }, Block("GREEN", 1).ConnectedBlockIds);
        CollectionAssert.AreEquivalent(new[] { "GREEN-C12", "GREEN-D14", "GREEN-A1" }, Block("GREEN", 13).ConnectedBlockIds);
        CollectionAssert.AreEquivalent(new[] { "GREEN-F27", "GREEN-G29", "GREEN-Z150" }, Block("GREEN", 28).ConnectedBlockIds);
        CollectionAssert.AreEquivalent(new[] { "GREEN-M76", "GREEN-N78", "GREEN-R101" }, Block("GREEN", 77).ConnectedBlockIds);
        CollectionAssert.AreEquivalent(new[] { "GREEN-N84", "GREEN-O86", "GREEN-Q100" }, Block("GREEN", 85).ConnectedBlockIds);
        CollectionAssert.AreEquivalent(new[] { "GREEN-Q99", "GREEN-N85" }, Block("GREEN", 100).ConnectedBlockIds);
    }
}
