using TrackModel.Core.Services;
using TrackModel.TestUI.Wpf.Services;
using TrackModel.TestUI.Wpf.ViewModels;
using TrainControl.Common.Communication;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;

namespace TrackModel.Wpf.Tests;

[STATestClass]
public class TestUiWorkflowTests
{
    private ModelConnection _connection = null!;
    private MainWindowViewModel _vm = null!;
    [TestInitialize]
    public void Initialize()
    {
        _connection = new ModelConnection();
        _vm = new MainWindowViewModel(_connection);
        _vm.RefreshAsync().GetAwaiter().GetResult();
    }
    [TestCleanup] public void Cleanup() => _vm.Stop();
    private CapturedBlockViewModel Output(string id) => _vm.OutputBlocks.Single(b => b.Id == id);

    [TestMethod]
    public void StableStartingState_IsCapturedFromModel()
    {
        Assert.AreEqual("OCCUPIED", _vm.SelectedOutput!.Occupancy);
        Assert.AreEqual("01", _vm.SelectedOutput.Train);
        Assert.AreEqual("22 mph", _vm.SelectedOutput.ActualSpeed);
        Assert.AreEqual("NORMAL", _vm.SelectedOutput.BrokenRail);
        Assert.AreEqual("NORMAL", _vm.SelectedOutput.TrackCircuit);
        Assert.AreEqual("NORMAL", _vm.SelectedOutput.Power);
    }

    [TestMethod]
    public async Task Refresh_FlushesImmediateEditsAndKeepsActualSpeedSeparate()
    {
        foreach (var speed in new[] { "30", "15", "30" })
        {
            _vm.Speed = speed;
            await _vm.RefreshAsync();
            Assert.AreEqual(speed + " mph", _vm.SelectedOutput!.Speed);
            Assert.AreEqual("22 mph", _vm.SelectedOutput.ActualSpeed);
            Assert.AreEqual("01", _vm.SelectedOutput.Train);
        }
    }

    [TestMethod]
    public async Task Authority_ChangesWithoutMovingTrain()
    {
        _vm.Speed = "30";
        foreach (var authority in new[] { "600", "0", "600" })
        {
            _vm.Authority = authority; await _vm.RefreshAsync();
            Assert.AreEqual(authority + " ft", _vm.SelectedOutput!.Authority);
            Assert.AreEqual("30 mph", _vm.SelectedOutput.Speed);
            Assert.AreEqual("22 mph", _vm.SelectedOutput.ActualSpeed);
            Assert.AreEqual("OCCUPIED", _vm.SelectedOutput.Occupancy);
        }
    }

    [TestMethod]
    public async Task Signals_MapToColorsAndTrackCircuitInstructions()
    {
        _vm.BlockId = "103";
        foreach (var (signal, label) in new[] { (SignalState.Green,"PROCEED"), (SignalState.Yellow,"CAUTION"), (SignalState.Red,"STOP"), (SignalState.Green,"PROCEED") })
        {
            _vm.Signal = signal; await _vm.RefreshAsync();
            Assert.AreEqual(signal.ToString().ToUpperInvariant(), _vm.SelectedOutput!.TrafficLight);
            Assert.AreEqual(label, _vm.SelectedOutput.TrackSignal);
            Assert.AreEqual("22 mph", Output("104").ActualSpeed);
        }
    }

    [TestMethod]
    public async Task ActualSpeed_DoesNotChangeCommandOrAuthority()
    {
        _vm.Speed = "30"; _vm.Authority = "600";
        foreach (var speed in new[] { "10", "0", "22" })
        {
            _vm.ActualSpeed = speed; await _vm.RefreshAsync();
            Assert.AreEqual(speed + " mph", _vm.SelectedOutput!.ActualSpeed);
            Assert.AreEqual("30 mph", _vm.SelectedOutput.Speed);
            Assert.AreEqual("600 ft", _vm.SelectedOutput.Authority);
        }
    }

    [TestMethod]
    public async Task MovementRemovalAndReplacement_ClearOldOccupancy()
    {
        _vm.CurrentBlock = "105"; await _vm.RefreshAsync();
        Assert.AreEqual("105", _vm.SelectedOutput!.Id);
        Assert.AreEqual("01", Output("105").Train);
        Assert.AreEqual("22 mph", Output("105").ActualSpeed);
        Assert.AreEqual("CLEAR", Output("104").Occupancy);
        _vm.RemoveTrainCommand.Execute(null); await _vm.RefreshAsync();
        Assert.AreEqual("CLEAR", Output("105").Occupancy);
        Assert.AreEqual("No train", Output("105").Train);
        _vm.CurrentBlock = "104"; _vm.ActualSpeed = "22"; _vm.TrainOccupancy = OccupancyState.Occupied;
        await _vm.RefreshAsync();
        Assert.AreEqual("01", Output("104").Train);
    }

    [TestMethod]
    public async Task Switch_ReportsLayoutBranchIds()
    {
        _vm.BlockId = "103"; Assert.IsTrue(_vm.HasSwitch);
        foreach (var (position, next) in new[] { (SwitchPosition.Normal,"104"), (SwitchPosition.Reverse,"121"), (SwitchPosition.Normal,"104") })
        {
            _vm.Switch = position; await _vm.RefreshAsync();
            Assert.AreEqual(position.ToString(), _vm.SelectedOutput!.Switch);
            Assert.AreEqual(next, _vm.SelectedOutput.NextBlock);
        }
    }

    [TestMethod]
    public async Task Crossing_IsIndependentOfSignal()
    {
        _vm.BlockId = "109"; Assert.IsTrue(_vm.HasCrossing);
        foreach (var crossing in new[] { CrossingState.Open, CrossingState.Closed, CrossingState.Open })
        {
            _vm.Crossing = crossing; await _vm.RefreshAsync();
            Assert.AreEqual(crossing.ToString(), _vm.SelectedOutput!.Crossing);
            Assert.AreEqual("No signal", _vm.SelectedOutput.TrackSignal);
        }
    }

    [TestMethod]
    public async Task Failures_AreIndependentAndPreservePhysicalTrainAndCommands()
    {
        _vm.Speed = "30"; _vm.Authority = "600";
        foreach (var (rail,circuit,power) in new[] { (true,false,false),(false,false,false),(false,true,false),(false,false,false),(false,false,true),(false,false,false),(true,true,true),(false,false,false) })
        {
            _vm.BrokenRail=rail; _vm.Circuit=circuit; _vm.Power=power; await _vm.RefreshAsync();
            var o = _vm.SelectedOutput!;
            Assert.AreEqual(rail ? "FAILURE" : "NORMAL",o.BrokenRail);
            Assert.AreEqual(circuit ? "FAILURE" : "NORMAL",o.TrackCircuit);
            Assert.AreEqual(power ? "FAILURE" : "NORMAL",o.Power);
            Assert.AreEqual(circuit || power ? "UNKNOWN" : "OCCUPIED",o.Occupancy);
            Assert.AreEqual("01",o.Train); Assert.AreEqual("22 mph",o.ActualSpeed);
            Assert.AreEqual("30 mph",o.Speed); Assert.AreEqual("600 ft",o.Authority);
        }
    }

    [TestMethod]
    public async Task PassengerExchange_IsExplicitAndRefreshNeverReplaysIt()
    {
        _vm.ActualSpeed="0"; _vm.Boarding="3"; _vm.Disembarking="2";
        await _vm.RefreshAsync();
        var b=_connection.Model.FindBlock("104")!;
        var waiting=b.WaitingPassengers; var tickets=b.TicketsSold; var boarding=b.BoardingPassengers; var exits=b.DisembarkingPassengers;
        _vm.SendTrainCommand.Execute(null); await _vm.RefreshAsync();
        Assert.AreEqual(waiting-3,b.WaitingPassengers); Assert.AreEqual(tickets+3,b.TicketsSold);
        Assert.AreEqual(boarding+3,b.BoardingPassengers); Assert.AreEqual(exits+2,b.DisembarkingPassengers);
        await _vm.RefreshAsync(); Assert.AreEqual(tickets+3,b.TicketsSold);
        _vm.SendTrainCommand.Execute(null); await _vm.RefreshAsync(); Assert.AreEqual(tickets+6,b.TicketsSold);
        _vm.Boarding="0"; _vm.Disembarking="0"; _vm.SendTrainCommand.Execute(null); await _vm.RefreshAsync();
        Assert.AreEqual(tickets+6,b.TicketsSold);
    }

    [TestMethod]
    public async Task BeaconAndPhysicalValues_FollowOutputBlock()
    {
        Assert.AreEqual("Station A",Output("104").Beacon);
        Assert.AreEqual("720 ft / 1.5%",Output("104").ElevationGrade);
        _vm.CurrentBlock="105"; await _vm.RefreshAsync();
        Assert.AreEqual("No station",_vm.SelectedOutput!.Beacon);
        Assert.AreEqual("720 ft / 1.5%",_vm.SelectedOutput.ElevationGrade);
    }

    [TestMethod]
    public async Task ClockStep_IsExactlyTenSecondsAtAnyMultiplierAndWrapsMidnight()
    {
        _vm.Time="10:00:00"; _vm.Multiplier="2"; await _vm.RefreshAsync();
        _vm.StepClockCommand.Execute(null); Assert.AreEqual("10:00:10",_vm.Time);
        _vm.StepClockCommand.Execute(null); Assert.AreEqual("10:00:20",_vm.Time);
        Assert.AreEqual(new TimeSpan(10,0,20),_connection.Model.SystemTime);
        _vm.Time="23:59:55"; await _vm.RefreshAsync(); _vm.StepClockCommand.Execute(null);
        Assert.AreEqual("00:00:05",_vm.Time); Assert.AreEqual(TimeSpan.FromDays(1)+TimeSpan.FromSeconds(5),_connection.Model.SystemTime);
    }

    [TestMethod]
    public async Task ClockMidnight_PreservesTicketsFromThePrecedingHour()
    {
        _vm.Time="23:59:55"; _vm.ActualSpeed="0"; await _vm.RefreshAsync();
        _vm.Boarding="3"; _vm.SendTrainCommand.Execute(null); await _vm.RefreshAsync();
        Assert.AreEqual(3,_connection.Model.CreateTicketSales("Blue").TicketsPerHour);
        _vm.StepClockCommand.Execute(null); await _vm.RefreshAsync();
        Assert.AreEqual("00:00:05",_vm.Time);
        Assert.AreEqual(3,_connection.Model.CreateTicketSales("Blue").TicketsPerHour);
        _vm.ToggleClockCommand.Execute(null); _vm.ToggleClockCommand.Execute(null);
        _vm.StepClockCommand.Execute(null);
        Assert.AreEqual(TimeSpan.FromDays(1)+TimeSpan.FromSeconds(15),_connection.Model.SystemTime);
        Assert.AreEqual(3,_connection.Model.CreateTicketSales("Blue").TicketsPerHour);
    }

    [TestMethod]
    public async Task OutputSelection_SurvivesRefreshAndDoesNotMoveTrain()
    {
        _vm.SelectedOutput=Output("105"); await _vm.RefreshAsync();
        Assert.AreEqual("105",_vm.SelectedOutput!.Id); Assert.AreEqual("CLEAR",_vm.SelectedOutput.Occupancy);
        Assert.AreEqual("104",_vm.CurrentBlock); Assert.AreEqual("01",Output("104").Train);
    }

    [TestMethod]
    public async Task RapidBlockChange_PreservesEachBlocksPendingCommand()
    {
        _vm.Speed="30"; _vm.BlockId="105"; _vm.Speed="15"; await _vm.RefreshAsync();
        Assert.AreEqual("30 mph",Output("104").Speed); Assert.AreEqual("15 mph",Output("105").Speed);
    }

    [TestMethod]
    public async Task InvalidInputs_KeepStateAndShowValidationAfterRefresh()
    {
        foreach(var speed in new[]{"-1","letters"})
        {
            _vm.Speed=speed; await _vm.RefreshAsync();
            Assert.AreEqual("25 mph",Output("104").Speed); StringAssert.Contains(_vm.Status,"nonnegative number");
        }
        _vm.Speed="25"; _vm.Authority="-1"; await _vm.RefreshAsync(); StringAssert.Contains(_vm.Status,"Authority must be a nonnegative number");
        Assert.AreEqual("1,200 ft",Output("104").Authority);
        _vm.Time="25:99:99"; await _vm.RefreshAsync(); StringAssert.Contains(_vm.Status,"HH:mm:ss");
        Assert.AreEqual(new TimeSpan(9,42,18),_connection.Model.SystemTime);
        Assert.IsFalse(_vm.HasSwitch); Assert.IsFalse(_vm.HasCrossing); Assert.IsFalse(_vm.HasSignal);
        Assert.AreEqual("N/A",Output("104").Switch); Assert.AreEqual("N/A",Output("104").Crossing);
    }


    [TestMethod]
    public async Task Temperature_UsesFahrenheitAndShowsHeaterAndPassengerTotals()
    {
        _vm.Temperature="32"; await _vm.RefreshAsync();
        Assert.AreEqual("32 °F",_vm.SelectedOutput!.Temperature);
        Assert.AreEqual("ON",_vm.SelectedOutput.Heater);
        _vm.Power=true; await _vm.RefreshAsync(); Assert.AreEqual("OFF",_vm.SelectedOutput.Heater);
        _vm.Power=false; _vm.Temperature="68"; await _vm.RefreshAsync(); Assert.AreEqual("OFF",_vm.SelectedOutput.Heater);
        Assert.AreEqual("12 / 8",_vm.SelectedOutput.PassengerTotals); Assert.AreEqual("12",_vm.SelectedOutput.Tickets);
    }

    [TestMethod]
    public async Task InvalidPassengerCount_IsRejectedWithoutRepeatingExchange()
    {
        _vm.ActualSpeed="0"; await _vm.RefreshAsync();
        foreach(var count in new[]{"-1","abc","1.5"})
        {
            _vm.Boarding=count; _vm.SendTrainCommand.Execute(null); await _vm.RefreshAsync();
            StringAssert.Contains(_vm.Status,"nonnegative whole number");
            Assert.AreEqual("12",Output("104").Tickets);
        }
        _vm.Boarding="999"; _vm.SendTrainCommand.Execute(null); await _vm.RefreshAsync();
        StringAssert.Contains(_vm.Status,"Boarding exceeds"); Assert.AreEqual("12",Output("104").Tickets);
    }

    private sealed class ModelConnection : IExternalModuleConnection
    {
        public TrackService Model { get; }=new();
        public event Action<string,MessageEnvelope>? MessageReceived;
        public event Action<string>? ErrorReported { add {} remove {} }
        public ModelConnection() => SampleTrackLayout.LoadDemo(Model);
        public Task SendAsync(object message)
        {
            try
            {
                switch(message)
                {
                    case TrackModelCommandMessage c: Model.ApplyCommand(c); break;
                    case TrackModelTrainUpdateMessage t: Model.ApplyTrainUpdate(t); break;
                    case TrackModelFailureCommandMessage f: Model.ApplyFailures(f); break;
                    case SystemTimeMessage t: Model.SetSystemTime(t.SystemTime); break;
                    case TrackModelTemperatureCommandMessage t: Model.ApplyTemperature(t); break;
                }
                var snapshot=Guid.NewGuid();
                if(message is TrackModelSnapshotRequestMessage)
                { var layout=Model.CreateLayoutMessage(); layout.SnapshotId=snapshot; Emit(layout); }
                foreach(var block in Model.Layout.Blocks)
                {
                    var state=Model.CreateBlockState(block.Id); state.SnapshotId=snapshot; Emit(state);
                    var environment=Model.CreateTrainEnvironment(block.Id); environment.SnapshotId=snapshot; Emit(environment);
                    Emit(new TrackModelSignalMessage{BlockId=block.Id,TrainId=environment.TrainId,Signal=environment.Signal});
                }
                foreach(var line in Model.Layout.Blocks.Select(b=>b.LineId).Distinct()) Emit(Model.CreateTicketSales(line));
                Emit(new TrackModelInputResultMessage{Accepted=true,MessageType=message.GetType().Name});
            }
            catch(ArgumentException ex) { Emit(new TrackModelInputResultMessage{Accepted=false,MessageType=message.GetType().Name,Detail=ex.Message}); }
            catch(InvalidOperationException ex) { Emit(new TrackModelInputResultMessage{Accepted=false,MessageType=message.GetType().Name,Detail=ex.Message}); }
            return Task.CompletedTask;
        }
        private void Emit(object message) => MessageReceived?.Invoke("Model",MessageSerializer.Deserialize(MessageSerializer.Serialize(message)));
    }
}
