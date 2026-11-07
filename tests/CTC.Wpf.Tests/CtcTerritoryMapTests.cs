using System.Collections.ObjectModel;
using System.Windows;
using CTC.Core.Models;
using CTC.Wpf.Controls;
using CTC.Wpf.ViewModels;

namespace CTC.Wpf.Tests;

/// <summary>Non-visual behaviour of the territory map control (no pixel testing).</summary>
[TestClass]
public class CtcTerritoryMapTests
{
    private static BlockViewModel Block(string blockId, int number, params string[] connectedTo)
    {
        var state = new CtcBlockState { BlockId = blockId, BlockNumber = number, Section = "A" };
        foreach (var id in connectedTo)
        {
            state.ConnectedBlockIds.Add(id);
        }

        return new BlockViewModel(state, "BLUE");
    }

    [STATestMethod]
    public void ClickingOnABlocksTrack_HitsThatBlock()
    {
        var blocks = new ObservableCollection<BlockViewModel> { Block("A1", 1, "A2"), Block("A2", 2, "A1") };
        var map = new CtcTerritoryMap { Blocks = blocks, LineId = "BLUE" };
        map.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        map.Arrange(new Rect(map.DesiredSize));

        var a2 = map.CurrentLayout.Placements["A2"].Midpoint;

        Assert.AreSame(blocks[1], map.HitTestBlock(a2));
        Assert.IsNull(map.HitTestBlock(new Point(1, 1)));
    }

    [STATestMethod]
    public void ChangingTheBlockCollection_RecomputesTheLayout()
    {
        var blocks = new ObservableCollection<BlockViewModel> { Block("A1", 1) };
        var map = new CtcTerritoryMap { Blocks = blocks, LineId = "BLUE" };
        Assert.HasCount(1, map.CurrentLayout.Placements);

        blocks.Add(Block("A2", 2));

        Assert.HasCount(2, map.CurrentLayout.Placements);
    }

    [STATestMethod]
    public void Zoom_IsClampedToTheMaximum()
    {
        var map = new CtcTerritoryMap { Zoom = 50 };

        Assert.AreEqual(CtcTerritoryMap.MaxZoom, map.Zoom);
    }
}
