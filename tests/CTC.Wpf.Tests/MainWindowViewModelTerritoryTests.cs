using CTC.Core.Interfaces;
using CTC.Core.Models;
using CTC.Core.Services;
using CTC.TestUI.Wpf.SampleData;
using CTC.Wpf.ViewModels;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace CTC.Wpf.Tests;

/// <summary>Selected-line filtering and selection behaviour behind the territory map.</summary>
[TestClass]
public class MainWindowViewModelTerritoryTests
{
    private sealed class NullMessageSender : IMessageSender
    {
        public Task SendAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
            where TMessage : class => Task.CompletedTask;
    }

    private static (CTCService Service, MainWindowViewModel ViewModel) CreateWithSampleLayout()
    {
        var service = new CTCService(new NullMessageSender());
        var viewModel = new MainWindowViewModel(service);
        service.ApplyTrackLayout(SampleTrackLayout.Create());
        return (service, viewModel);
    }

    private static CtcLineState Line(MainWindowViewModel viewModel, string lineId) =>
        viewModel.AvailableLines.Single(line => line.LineId == lineId);

    [TestMethod]
    public void SampleLayout_OffersAllThreeLines_AndSelectsTheFirst()
    {
        var (_, viewModel) = CreateWithSampleLayout();

        CollectionAssert.AreEqual(new[] { "Blue Line", "Red Line", "Green Line" }, viewModel.AvailableLines.Select(line => line.Name).ToArray());
        Assert.AreEqual("BLUE", viewModel.SelectedLine?.LineId);
        Assert.HasCount(241, viewModel.Blocks);
        Assert.HasCount(15, viewModel.SelectedLineBlocks);
    }

    [TestMethod]
    public void ChangingSelectedLine_ShowsOnlyThatLinesBlocks()
    {
        var (_, viewModel) = CreateWithSampleLayout();

        viewModel.SelectedLine = Line(viewModel, "GREEN");

        Assert.HasCount(150, viewModel.SelectedLineBlocks);
        Assert.IsTrue(viewModel.SelectedLineBlocks.All(block => block.LineId == "GREEN"));

        viewModel.SelectedLine = Line(viewModel, "RED");

        Assert.HasCount(76, viewModel.SelectedLineBlocks);
        Assert.IsTrue(viewModel.SelectedLineBlocks.All(block => block.BlockId.StartsWith("RED-")));
    }

    [TestMethod]
    public void SelectedLineBlocks_AreTheSameAdaptersAsBlocks()
    {
        var (_, viewModel) = CreateWithSampleLayout();

        viewModel.SelectedLine = Line(viewModel, "RED");

        Assert.IsTrue(viewModel.SelectedLineBlocks.All(block => viewModel.Blocks.Contains(block)));
    }

    [TestMethod]
    public void SelectingABlockOfTheCurrentLine_SetsSelectedBlock()
    {
        var (_, viewModel) = CreateWithSampleLayout();
        viewModel.SelectedLine = Line(viewModel, "GREEN");
        var block = viewModel.SelectedLineBlocks.Single(block => block.BlockId == "GREEN-W132");

        viewModel.SelectedBlock = block;

        Assert.AreSame(block, viewModel.SelectedBlock);
        Assert.AreEqual("W132", block.DisplayName);
        Assert.AreEqual("Inglewood", block.StationName);
    }

    [TestMethod]
    public void SwitchingLine_ClearsSelectedBlockOfTheOtherLine()
    {
        var (_, viewModel) = CreateWithSampleLayout();
        viewModel.SelectedBlock = viewModel.SelectedLineBlocks.First(block => block.BlockId == "A5");

        viewModel.SelectedLine = Line(viewModel, "RED");

        Assert.IsNull(viewModel.SelectedBlock);
    }

    [TestMethod]
    public void ResendingTheLayout_KeepsTheSelectedLineAndBlock()
    {
        var (service, viewModel) = CreateWithSampleLayout();
        viewModel.SelectedLine = Line(viewModel, "GREEN");
        viewModel.SelectedBlock = viewModel.SelectedLineBlocks.Single(block => block.BlockId == "GREEN-K65");

        service.ApplyTrackLayout(SampleTrackLayout.Create());

        Assert.AreEqual("GREEN", viewModel.SelectedLine?.LineId);
        Assert.AreEqual("GREEN-K65", viewModel.SelectedBlock?.BlockId);
        Assert.Contains(viewModel.SelectedBlock!, viewModel.SelectedLineBlocks);
    }

    [TestMethod]
    public void OccupiedBlockCount_CountsTheDisplayedLineOnly()
    {
        var (service, viewModel) = CreateWithSampleLayout();
        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "A2", Occupancy = OccupancyState.Occupied });
        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "GREEN-I40", Occupancy = OccupancyState.Occupied });
        service.ApplyBlockStatus(new BlockStatusMessage { BlockId = "GREEN-I41", Occupancy = OccupancyState.Occupied });

        Assert.AreEqual(1, viewModel.OccupiedBlockCount);

        viewModel.SelectedLine = Line(viewModel, "GREEN");
        Assert.AreEqual(2, viewModel.OccupiedBlockCount);

        viewModel.SelectedLine = Line(viewModel, "RED");
        Assert.AreEqual(0, viewModel.OccupiedBlockCount);
    }

    [TestMethod]
    public void BlockStatus_UpdatesTheSameAdapterTheMapDraws()
    {
        var (service, viewModel) = CreateWithSampleLayout();
        viewModel.SelectedLine = Line(viewModel, "RED");
        var block = viewModel.SelectedLineBlocks.Single(block => block.BlockId == "RED-H27");
        var changed = new List<string?>();
        block.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        service.ApplyBlockStatus(new BlockStatusMessage
        {
            BlockId = "RED-H27",
            Occupancy = OccupancyState.Occupied,
            Switch = SwitchPosition.Reverse,
            Maintenance = MaintenanceState.Closed,
        });

        Assert.IsNotEmpty(changed, "The map relies on PropertyChanged to redraw a block.");
        Assert.AreEqual(OccupancyState.Occupied, block.OccupancyState);
        Assert.AreEqual(SwitchPosition.Reverse, block.SwitchPosition);
        Assert.AreEqual(MaintenanceState.Closed, block.MaintenanceState);
    }

    [TestMethod]
    public void ChangingLine_StillScopesScheduleBuilderAndDispatchQueue()
    {
        var (service, viewModel) = CreateWithSampleLayout();
        var train = new ScheduledTrain { TrainId = "000", LineId = "BLUE" };
        train.Route.Add(new ScheduledRouteBlock { BlockId = "A1", ArrivalTime = new TimeSpan(12, 0, 0) });
        foreach (var blockId in new[] { "A2", "A3", "A4", "A5", "B6", "B7", "B8", "B9" })
        {
            train.Route.Add(new ScheduledRouteBlock { BlockId = blockId });
        }

        train.Route.Add(new ScheduledRouteBlock { BlockId = "B10", ArrivalTime = new TimeSpan(12, 5, 0) });
        service.QueueSchedule([train]);

        Assert.AreSame(viewModel.SelectedLine, viewModel.ScheduleBuilder.Line);
        Assert.AreEqual(1, viewModel.QueuedTrainCount);

        viewModel.SelectedLine = Line(viewModel, "GREEN");

        Assert.AreSame(viewModel.SelectedLine, viewModel.ScheduleBuilder.Line);
        Assert.AreEqual(0, viewModel.QueuedTrainCount);
        Assert.HasCount(150, viewModel.DestinationBlockIds);
    }
}
