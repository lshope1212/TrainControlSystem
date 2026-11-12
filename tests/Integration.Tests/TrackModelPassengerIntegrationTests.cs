using TrackModel.Core.Services;
using TrackModel.Wpf.Services;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;
using TrainModel.Core.Services;
using TrainModel.Wpf.Services;
using TrainVm = TrainModel.Wpf.ViewModels.MainWindowViewModel;

namespace TrainControl.Integration.Tests;

/// <summary>Track Model changes exercised against the unchanged neighboring implementations.</summary>
[TestClass]
public class TrackModelPassengerIntegrationTests
{
    [TestMethod]
    public async Task StationArrivalsReachUnchangedCtc_AndBoardingDoesNotSellMoreTickets()
    {
        await using var system = new LiveSystem();
        await system.StartCtcAsync();
        await system.StartTrackModelAsync(prepare: track =>
        {
            track.RecordStationArrivals("10", 30);
            track.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "10" });
        });
        await LiveSystem.UntilAsync(() => system.OnCtc(ctc => ctc.State.FindLine("Blue")?.TicketSalesPerHour == 30),
            "CTC receives sales without any boarding.");
        await system.SendToTrackModelAsync(new PassengerExchangeMessage { TrainId = "001", Boarded = 20, Alighted = 2 });
        await LiveSystem.UntilAsync(() => system.OnTrackModel(track => track.FindBlock("10")!.BoardingPassengers == 20),
            "the existing exchange message updates station counts.");
        Assert.AreEqual(30, await system.OnCtc(ctc => ctc.State.FindLine("Blue")!.TicketSalesPerHour));
        await system.OnTrackModel(track => { track.SetSystemTime(track.SystemTime + TimeSpan.FromHours(1)); return true; });
        await LiveSystem.UntilAsync(() => system.OnCtc(ctc => ctc.State.FindLine("Blue")!.TicketSalesPerHour == 0),
            "the hourly ticket ledger expires through the existing CTC contract.");
        Assert.AreEqual(30, await system.OnTrackModel(track => track.FindBlock("10")!.TicketsSold));
    }

    [TestMethod]
    [DataRow(30)]
    [DataRow(500)]
    public async Task ExistingTrainModelReceivesDemandAndConfiguredBeacon_AndItsExchangeReachesTrackModel(int arrivals)
    {
        using var trackThread = new DispatcherThread("Passenger Track Model");
        using var trainThread = new DispatcherThread("Passenger Train Model");
        using var stop = new CancellationTokenSource();
        var prefix = "TrackModel.Passengers." + Guid.NewGuid().ToString("N");
        var endpoints = new TrackModelEndpoints(prefix, prefix + ".Wayside", prefix + ".Train", prefix + ".Ctc", prefix + ".TestUi");
        var fleet = new TrainFleetService();
        var vm = await trainThread.GetAsync(() => new TrainVm(fleet, new TrainModelOutputPublisher(endpoints.Inbound, endpoints.TrainController)));
        await trainThread.DoAsync(() => vm.ApplyTrainPlacement("001", "10", 0));
        var receiver = new TrainModelNamedPipeReceiver(vm, trainThread.Dispatcher, endpoints.TrainModel);
        var receive = trainThread.StartAsync(() => receiver.RunAsync(stop.Token));
        var track = await trackThread.GetAsync(() =>
        {
            var model = new TrackService();
            var layout = BlueLineTrackLayout.Create();
            // Explicit test metadata: beacon at this station's surveyed stopping point.
            var station = layout.Blocks.Single(b => b.Id == "10");
            station.Beacon = station.StationName;
            station.BeaconDistanceToStopMeters = 0;
            station.BeaconPlatformSide = PlatformSide.Right;
            model.LoadLayout(layout);
            model.ApplyPassengerDemand(new() { BlockId = "10", WaitingPassengers = 0 });
            model.RecordStationArrivals("10", arrivals);
            model.ApplyTrainUpdate(new() { TrainId = "001", CurrentBlockId = "10" });
            return model;
        });
        var connection = await trackThread.GetAsync(() => new TrackModelConnection(track, trackThread.Dispatcher, endpoints));
        var run = trackThread.StartAsync(() => connection.RunAsync(stop.Token));
        try
        {
            await LiveSystem.UntilAsync(() => trainThread.GetAsync(() => vm.EventLog.Any(entry => entry.Message.Contains("beacon received"))),
                "the unchanged Train Model beacon handler accepts the typed beacon after the demand input.");
            await trainThread.DoAsync(() =>
            {
                vm.ApplySystemTime(new() { SystemTime = TimeSpan.Zero });
                vm.ApplySystemTime(new() { SystemTime = TimeSpan.FromMilliseconds(100) });
            });
            await LiveSystem.UntilAsync(() => trainThread.GetAsync(() => fleet.GetTrain("001")!.CurrentState.BeaconPlatformSide == "Right"),
                "the unchanged Train Model applies the typed beacon on its next simulation tick.");
            await trainThread.DoAsync(() => vm.ExchangePassengersCommand.Execute(null));
            var boarded = await trainThread.GetAsync(() => Math.Min(arrivals, fleet.GetTrain("001")!.Train.Passengers.Capacity));
            Assert.AreEqual(boarded, (await trainThread.GetAsync(() => fleet.GetTrain("001")!.CurrentState)).LastBoarded,
                "The unchanged Train Model uses demand delivered by Track Model.");
            await LiveSystem.UntilAsync(() => trackThread.GetAsync(() => track.FindBlock("10")!.BoardingPassengers == boarded),
                "the unchanged Train Model publisher's exchange updates the Track Model.");
            var train = await trainThread.GetAsync(() => fleet.GetTrain("001")!.CurrentState);
            Assert.AreEqual(await trackThread.GetAsync(() => track.FindBlock("10")!.StationName), train.BeaconStationName);
            Assert.AreEqual("Right", train.BeaconPlatformSide);
            Assert.AreEqual(arrivals, await trackThread.GetAsync(() => track.CreateTicketSales("Blue").TicketsPerHour));
            Assert.AreEqual(arrivals - boarded, await trackThread.GetAsync(() => track.FindBlock("10")!.WaitingPassengers));
            Assert.AreEqual(arrivals - boarded, train.LastLeftBehind);
        }
        finally
        {
            await trackThread.DoAsync(connection.Stop);
            await stop.CancelAsync();
            await Task.WhenAll(run, receive).WaitAsync(TimeSpan.FromSeconds(8));
        }
    }
}
