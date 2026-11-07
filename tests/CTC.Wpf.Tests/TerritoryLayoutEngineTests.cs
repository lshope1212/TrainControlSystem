using CTC.Core.Models;
using CTC.TestUI.Wpf.SampleData;
using CTC.Wpf.Layout;
using CTC.Wpf.ViewModels;

namespace CTC.Wpf.Tests;

[TestClass]
public class TerritoryLayoutEngineTests
{
    /// <summary>The fixture's blocks of one line, as the map receives them.</summary>
    private static List<BlockViewModel> LineBlocks(string lineId)
    {
        var line = SampleTrackLayout.Create().Lines.Single(line => line.LineId == lineId);
        return line.Blocks.Select(definition =>
        {
            var state = new CtcBlockState
            {
                BlockId = definition.BlockId,
                BlockNumber = definition.BlockNumber,
                Section = definition.Section,
                LengthMeters = definition.LengthMeters,
            };
            foreach (var id in definition.ConnectedBlockIds)
            {
                state.ConnectedBlockIds.Add(id);
            }

            return new BlockViewModel(state, lineId);
        }).ToList();
    }

    private static BlockViewModel Block(string blockId, int number, string section, params string[] connectedTo)
    {
        var state = new CtcBlockState { BlockId = blockId, BlockNumber = number, Section = section };
        foreach (var id in connectedTo)
        {
            state.ConnectedBlockIds.Add(id);
        }

        return new BlockViewModel(state);
    }

    private static void AssertWellFormed(TerritoryLayout layout, IReadOnlyCollection<BlockViewModel> blocks)
    {
        Assert.HasCount(blocks.Count, layout.Placements);
        foreach (var block in blocks)
        {
            Assert.IsTrue(layout.Placements.ContainsKey(block.BlockId), $"{block.BlockId} was not placed.");
        }

        Assert.IsTrue(layout.Bounds.Width > 0 && layout.Bounds.Height > 0);
        Assert.IsTrue(double.IsFinite(layout.Bounds.Width) && double.IsFinite(layout.Bounds.Height));

        foreach (var placement in layout.Placements.Values)
        {
            Assert.IsGreaterThanOrEqualTo(2, placement.Points.Count);
            foreach (var point in placement.Points.Append(placement.Midpoint))
            {
                Assert.IsTrue(double.IsFinite(point.X) && double.IsFinite(point.Y), $"{placement.BlockId} has a non-finite point.");
                Assert.IsTrue(layout.Bounds.Contains(point), $"{placement.BlockId} lies outside the bounds.");
            }

            Assert.IsTrue(double.IsFinite(placement.Direction.X) && double.IsFinite(placement.Direction.Y));
        }
    }

    [TestMethod]
    [DataRow("BLUE", 15)]
    [DataRow("RED", 76)]
    [DataRow("GREEN", 150)]
    public void CourseLines_PlaceEveryBlockFromTheirSchematic(string lineId, int blockCount)
    {
        var blocks = LineBlocks(lineId);

        var layout = TerritoryLayoutEngine.Compute(lineId, blocks);

        Assert.HasCount(blockCount, blocks);
        AssertWellFormed(layout, blocks);
        Assert.IsFalse(layout.UsedFallback, "Every section of the course lines should have schematic geometry.");
    }

    [TestMethod]
    [DataRow("BLUE")]
    [DataRow("RED")]
    [DataRow("GREEN")]
    public void CourseLines_EverySectionHasAPath(string lineId)
    {
        var schematic = TerritoryLayoutProvider.Find(lineId);

        Assert.IsNotNull(schematic);
        foreach (var section in LineBlocks(lineId).Select(block => block.Section).Distinct())
        {
            Assert.IsNotNull(schematic.FindSection(section), $"{lineId} section {section} has no path.");
        }
    }

    [TestMethod]
    public void Provider_MatchesLineIdsAndNames()
    {
        Assert.AreSame(TerritoryLayoutProvider.Green, TerritoryLayoutProvider.Find("GREEN"));
        Assert.AreSame(TerritoryLayoutProvider.Green, TerritoryLayoutProvider.Find("Green Line"));
        Assert.AreSame(TerritoryLayoutProvider.Red, TerritoryLayoutProvider.Find("red"));
        Assert.IsNull(TerritoryLayoutProvider.Find("Purple Line"));
        Assert.IsNull(TerritoryLayoutProvider.Find(null));
    }

    [TestMethod]
    public void Connections_AreDeduplicatedAndComeFromConnectedBlockIds()
    {
        var blocks = LineBlocks("BLUE");

        var layout = TerritoryLayoutEngine.Compute("BLUE", blocks);

        // Each undirected A<->B pair once: 14 links for the 15-block Blue tree.
        Assert.HasCount(14, layout.Connections);
        Assert.HasCount(14, layout.Connections.Select(c => (c.FromBlockId, c.ToBlockId)).Distinct());
        Assert.IsTrue(layout.Connections.Any(c => c is { FromBlockId: "A5", ToBlockId: "C11" } or { FromBlockId: "C11", ToBlockId: "A5" }));

        // B10 and C11 are consecutive numbers but not connected track.
        Assert.IsFalse(layout.Connections.Any(c => c is { FromBlockId: "B10", ToBlockId: "C11" } or { FromBlockId: "C11", ToBlockId: "B10" }));
    }

    [TestMethod]
    public void Connections_IgnoreBlocksNotOnTheLine()
    {
        var blocks = new[] { Block("A1", 1, "A", "A2", "ELSEWHERE"), Block("A2", 2, "A", "A1") };

        var layout = TerritoryLayoutEngine.Compute("BLUE", blocks);

        Assert.HasCount(1, layout.Connections);
    }

    [TestMethod]
    public void Yards_AreVisualTerminalsJoinedToTheirBlocks()
    {
        var green = TerritoryLayoutEngine.Compute("GREEN", LineBlocks("GREEN"));
        var red = TerritoryLayoutEngine.Compute("RED", LineBlocks("RED"));

        CollectionAssert.AreEquivalent(new[] { "GREEN-I57", "GREEN-K63" }, green.Terminals.Single().BlockIds.ToArray());
        CollectionAssert.AreEquivalent(new[] { "RED-C9" }, red.Terminals.Single().BlockIds.ToArray());
    }

    [TestMethod]
    public void UnknownLine_FallsBackToRows()
    {
        var blocks = Enumerable.Range(1, 45)
            .Select(n => Block($"P{n}", n, n <= 30 ? "A" : "B", n > 1 ? $"P{n - 1}" : "P2"))
            .ToList();

        var layout = TerritoryLayoutEngine.Compute("PURPLE", blocks);

        AssertWellFormed(layout, blocks);
        Assert.IsTrue(layout.UsedFallback);
        Assert.IsEmpty(layout.Terminals);
    }

    [TestMethod]
    public void UnknownSection_OnAKnownLine_FallsBack()
    {
        var blocks = LineBlocks("BLUE");
        blocks.Add(Block("D16", 16, "D", "C15"));

        var layout = TerritoryLayoutEngine.Compute("BLUE", blocks);

        AssertWellFormed(layout, blocks);
        Assert.IsTrue(layout.UsedFallback);
    }

    [TestMethod]
    public void NoBlocks_GivesAnEmptyLayout()
    {
        var layout = TerritoryLayoutEngine.Compute("GREEN", []);

        Assert.IsEmpty(layout.Placements);
        Assert.IsTrue(layout.Bounds.Width > 0 && layout.Bounds.Height > 0);
    }

    [TestMethod]
    public void Blocks_AreLaidOutInBlockNumberOrderAlongTheSection()
    {
        var layout = TerritoryLayoutEngine.Compute("GREEN", LineBlocks("GREEN"));

        // Section I runs left to right on the schematic, from I36 to I57.
        var xs = Enumerable.Range(36, 22).Select(n => layout.Placements[$"GREEN-I{n}"].Midpoint.X).ToList();
        CollectionAssert.AreEqual(xs.Order().ToList(), xs);
    }
}
