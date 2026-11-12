using CTC.Core.Interfaces;
using CTC.Core.Models;
using CTC.Core.Services;
using CTC.TestUI.Wpf.SampleData;
using CTC.Wpf.ViewModels;
using TrainControl.Contracts.Messages;

namespace CTC.Wpf.Tests;

/// <summary>
/// The Schedule Builder's per-train starting-block selectors: only the selected line's yard entries
/// are offered, each train chooses independently, the start cell is marked, and a line or layout
/// without yard entries cannot be scheduled.
/// </summary>
[TestClass]
public class ScheduleBuilderStartBlockTests
{
    private sealed class NullMessageSender : IMessageSender
    {
        public Task SendAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
            where TMessage : class => Task.CompletedTask;
    }

    /// <summary>A five-block line A1-A2-A3 with a branch A3-B4 / A3-C5; yard entries as given (test fixture).</summary>
    private static TrackLayoutMessage Layout(params string[] yardEntries)
    {
        TrackBlockDefinition Block(string id, int number, params string[] connected) => new()
        {
            BlockId = id, BlockNumber = number, Section = id[..1], LengthMeters = 100, SpeedLimitKilometersPerHour = 50,
            ConnectedBlockIds = [.. connected], IsYardEntry = yardEntries.Contains(id),
        };

        return new TrackLayoutMessage
        {
            Lines =
            {
                new TrackLineDefinition
                {
                    LineId = "TEST", Name = "Test Line",
                    Blocks = { Block("A1", 1, "A2"), Block("A2", 2, "A1", "A3"), Block("A3", 3, "A2", "B4", "C5"), Block("B4", 4, "A3"), Block("C5", 5, "A3") },
                },
            },
        };
    }

    private static (CTCService Service, MainWindowViewModel ViewModel) Create(TrackLayoutMessage layout)
    {
        var service = new CTCService(new NullMessageSender());
        var viewModel = new MainWindowViewModel(service);
        service.ApplyTrackLayout(layout);
        return (service, viewModel);
    }

    private static ScheduleTimeCellViewModel Cell(ScheduleBuilderViewModel builder, string blockId, int column) =>
        builder.Rows.Single(row => row.BlockId == blockId).TrainTimes[column];

    private static ScheduleBuilderViewModel Generate(MainWindowViewModel viewModel, int trains)
    {
        var builder = viewModel.ScheduleBuilder;
        builder.NumberOfTrainsText = trains.ToString();
        builder.GenerateTemplateCommand.Execute(null);
        return builder;
    }

    [TestMethod]
    public void GreenLine_OffersOnlyItsYardEntry_PreselectedForEachTrain()
    {
        var (_, viewModel) = Create(SampleTrackLayout.Create());
        viewModel.SelectedLine = viewModel.AvailableLines.Single(line => line.LineId == "GREEN");

        var builder = Generate(viewModel, 2);

        Assert.AreEqual("Starting blocks (yard entries): K63 (GREEN-K63)", builder.YardEntryNotice);
        Assert.HasCount(2, builder.TrainStarts);
        foreach (var start in builder.TrainStarts)
        {
            CollectionAssert.AreEqual(new[] { new StartBlockOption("GREEN-K63", "K63 (GREEN-K63)") }, start.Options.ToArray());
            Assert.AreEqual("GREEN-K63", start.SelectedBlockId);
        }

        Assert.IsTrue(Cell(builder, "GREEN-K63", 1).IsStartBlock);
        Assert.IsFalse(Cell(builder, "GREEN-A1", 0).IsStartBlock, "Not the lowest-numbered block.");
        Assert.AreEqual("Departure time from this train's starting block.", Cell(builder, "GREEN-K63", 0).CellToolTip);
        Assert.IsTrue(builder.Rows.Single(row => row.BlockId == "GREEN-K63").IsYardEntry);
        Assert.IsFalse(builder.Rows.Single(row => row.BlockId == "GREEN-D14").IsYardEntry, "GREEN 14 is not a yard entry.");
    }

    [TestMethod]
    public void SeveralYardEntries_EachTrainSelectsItsOwn_AndTheScheduleQueuesFromThem()
    {
        var (service, viewModel) = Create(Layout("A1", "C5"));
        var builder = Generate(viewModel, 2);

        Assert.IsTrue(builder.TrainStarts.All(start => start.IsMissing), "With several entries nothing is preselected.");
        CollectionAssert.AreEqual(new[] { "A1", "C5" }, builder.TrainStarts[0].Options.Select(option => option.BlockId).ToArray());

        builder.TrainStarts[0].SelectedBlockId = "A1";
        builder.TrainStarts[1].SelectedBlockId = "C5";
        Cell(builder, "A1", 0).TimeText = "12:00:00";
        Cell(builder, "B4", 0).TimeText = "12:02:00";
        Cell(builder, "C5", 1).TimeText = "12:01:00";
        Cell(builder, "A1", 1).TimeText = "12:03:00";
        Assert.IsTrue(Cell(builder, "C5", 1).IsStartBlock);
        Assert.IsFalse(Cell(builder, "C5", 0).IsStartBlock);

        builder.QueueScheduleCommand.Execute(null);

        StringAssert.StartsWith(builder.StatusMessage, "Queued 2 train(s) for Test Line.");
        CollectionAssert.AreEqual(new[] { "A1", "C5" }, service.State.ScheduledTrains.Select(train => train.StartBlockId).ToArray());
        CollectionAssert.AreEqual(new[] { "C5", "A3", "A2", "A1" }, service.State.ScheduledTrains[1].Route.Select(block => block.BlockId).ToArray());
    }

    [TestMethod]
    public void ChangingATrainsStart_MovesItsDeparture_HighlightsIt_AndReports()
    {
        var (_, viewModel) = Create(Layout("A1", "C5"));
        var builder = Generate(viewModel, 1);
        builder.TrainStarts[0].SelectedBlockId = "A1";
        Cell(builder, "A1", 0).TimeText = "12:00:00";
        Cell(builder, "B4", 0).TimeText = "12:02:00";

        builder.TrainStarts[0].SelectedBlockId = "C5";

        Assert.AreEqual(string.Empty, Cell(builder, "A1", 0).TimeText);
        Assert.AreEqual("12:00:00", Cell(builder, "C5", 0).TimeText);
        Assert.IsTrue(Cell(builder, "C5", 0).IsStartBlock);
        Assert.IsTrue(Cell(builder, "C5", 0).NeedsReconsidering);
        StringAssert.Contains(builder.StatusMessage, "moved from A1 to C5");

        // Editing the moved time clears its highlight.
        Cell(builder, "C5", 0).TimeText = "12:00:05";
        Assert.IsFalse(Cell(builder, "C5", 0).NeedsReconsidering);
    }

    [TestMethod]
    public void LineWithoutYardEntries_ExplainsWhy_AndCannotBeScheduled()
    {
        var (_, viewModel) = Create(Layout());

        var builder = Generate(viewModel, 1);

        Assert.IsTrue(builder.HasNoYardEntries);
        StringAssert.StartsWith(builder.YardEntryNotice, "Test Line has no yard-entry blocks in its track layout");
        StringAssert.StartsWith(builder.StatusMessage, "Schedule template not generated: Test Line has no yard-entry blocks");
        Assert.IsEmpty(builder.Rows);
        Assert.IsFalse(builder.QueueScheduleCommand.CanExecute(null));
    }

    [TestMethod]
    public void ChangedYardEntriesInTheLayout_DiscardTheObsoleteTemplate()
    {
        var (service, viewModel) = Create(Layout("A1", "C5"));
        var builder = Generate(viewModel, 1);
        builder.TrainStarts[0].SelectedBlockId = "C5";

        service.ApplyTrackLayout(Layout("A1"));

        Assert.IsEmpty(builder.Rows);
        Assert.IsEmpty(builder.TrainStarts);
        StringAssert.Contains(builder.StatusMessage, "the track layout changed (its yard entries)");
        Assert.AreEqual("Starting blocks (yard entries): A1", builder.YardEntryNotice);
    }

    [TestMethod]
    public void CsvUpload_ListsTrainsWhoseStartMustStillBeChosen()
    {
        var (_, viewModel) = Create(Layout("A1", "C5"));
        var builder = viewModel.ScheduleBuilder;

        builder.LoadCsv("Line,Section,Block Number,Train 1,Train 2\nTest,A,1,12:00:00,\nTest,B,4,12:02:00,12:03:00\n", "sample.csv");

        Assert.AreEqual("A1", builder.TrainStarts[0].SelectedBlockId);
        Assert.IsNull(builder.TrainStarts[1].SelectedBlockId);
        StringAssert.Contains(builder.StatusMessage, "Choose a starting block for Train 002");
    }
}
