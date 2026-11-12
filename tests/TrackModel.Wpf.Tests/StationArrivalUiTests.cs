using TrackModel.Core.Services;
using TrackModel.Wpf.ViewModels;

namespace TrackModel.Wpf.Tests;

[STATestClass]
public class StationArrivalUiTests
{
    [TestMethod]
    public void RecordArrivalsUsesTheSelectedStationAndNeverCreatesATrain()
    {
        var track = new TrackService();
        CourseTrackLayout.Load(track);
        var vm = new MainWindowViewModel(track);
        vm.SelectedLine = vm.Lines.Single(line => line.Id == "Green");
        var station = vm.SelectedStation!;
        var waiting = track.FindBlock(station.Id)!.WaitingPassengers;
        vm.ArrivingCustomers = "30";
        vm.RecordArrivalsCommand.Execute(null);
        Assert.AreEqual(waiting + 30, track.FindBlock(station.Id)!.WaitingPassengers);
        Assert.AreEqual(30, track.CreateTicketSales("Green").TicketsPerHour);
        Assert.AreEqual(0, track.CreateTicketSales("Blue").TicketsPerHour);
        Assert.IsFalse(track.Layout.Blocks.Any(block => block.IsOccupied));
        StringAssert.Contains(vm.Status, "30 tickets sold");
    }

    [TestMethod]
    public void InvalidArrivalsLeaveCountsUnchanged()
    {
        var track = new TrackService();
        BlueLineTrackLayout.Load(track);
        var vm = new MainWindowViewModel(track);
        var station = vm.SelectedStation!;
        var waiting = track.FindBlock(station.Id)!.WaitingPassengers;
        foreach (var input in new[] { "0", "-1", "abc", "1.5", "2147483647" })
        {
            vm.ArrivingCustomers = input;
            vm.RecordArrivalsCommand.Execute(null);
            Assert.AreEqual(waiting, track.FindBlock(station.Id)!.WaitingPassengers);
            Assert.AreEqual(0, track.CreateTicketSales("Blue").TicketsPerHour);
        }
    }
}
