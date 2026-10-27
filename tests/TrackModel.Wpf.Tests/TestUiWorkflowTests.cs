using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    [TestMethod]
    public async Task TrainPresenceSelector_RendersYesNoAndUpdatesPhysicalPresence()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/TrackModel.TestUI.Wpf;component/Resources/Theme.xaml", UriKind.Relative)
        });
        var window = new TrackModel.TestUI.Wpf.MainWindow(_vm) { ShowActivated = false };
        void Render()
        {
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        try
        {
            window.Show(); Render();
            var selector = Descendants(window).OfType<ComboBox>()
                .Single(control => AutomationProperties.GetName(control) == "Train present");
            Assert.IsTrue(Descendants(selector).OfType<TextBlock>().Any(text => text.Text == "Yes"));

            selector.SelectedItem = false;
            await _vm.RefreshAsync(); Render();
            Assert.IsFalse(_vm.TrainPresent);
            Assert.AreEqual("No train", Output("104").Train);
            Assert.IsTrue(Descendants(selector).OfType<TextBlock>().Any(text => text.Text == "No"));

            // A captured model snapshot must also update the selector, not just user edits.
            _connection.Model.ApplyTrainUpdate(new TrackModelTrainUpdateMessage
                { TrainId = "01", CurrentBlockId = "104", ActualSpeedMetersPerSecond = 0 });
            await _vm.RefreshAsync(); Render();
            Assert.IsTrue(_vm.TrainPresent);
            Assert.IsTrue(Descendants(selector).OfType<TextBlock>().Any(text => text.Text == "Yes"));
        }
        finally { window.Close(); }
    }

    [TestMethod]
    public async Task DashboardTrainInformation_UpdatesForSelectedCommandBlock()
    {
        await UseBlueLineAsync();
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/TrackModel.Wpf;component/Resources/Theme.xaml", UriKind.Relative)
        });
        var dashboard = new TrackModel.Wpf.ViewModels.MainWindowViewModel(_connection.Model);
        var window = new TrackModel.Wpf.MainWindow(dashboard) { ShowActivated = false };
        void Render()
        {
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        string Display(string name) => Descendants(window).OfType<TextBlock>()
            .Single(text => AutomationProperties.GetName(text) == name).Text;
        try
        {
            window.Show(); Render();
            _vm.BlockId = "9"; _vm.Speed = "30"; _vm.Authority = "600";
            await _vm.RefreshAsync(); Render();

            // Inspecting another block must not show commands sent to block 9.
            Assert.AreEqual("Block 1", Display("Train information block"));
            Assert.AreEqual("0 mph", Display("Train commanded speed"));
            Assert.AreEqual("0 ft", Display("Train authority"));

            dashboard.SelectedBlock = dashboard.Blocks.Single(block => block.Id == "9");
            Render();
            Assert.AreEqual("Block 9", Display("Train information block"));
            Assert.AreEqual("30 mph", Display("Train commanded speed"));
            Assert.AreEqual("600 ft", Display("Train authority"));

            // Later edits must update rendered dashboard bindings automatically,
            // through the input debounce, without clicking Refresh outputs.
            _vm.Speed = "15"; _vm.Authority = "300";
            var frame = new DispatcherFrame();
            var deadline = DateTime.UtcNow.AddSeconds(3);
            var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            poll.Tick += (_, _) =>
            {
                if (Display("Train commanded speed") == "15 mph" && Display("Train authority") == "300 ft"
                    || DateTime.UtcNow >= deadline) frame.Continue = false;
            };
            try { poll.Start(); Dispatcher.PushFrame(frame); }
            finally { poll.Stop(); }
            Render();
            Assert.AreEqual("15 mph", Display("Train commanded speed"));
            Assert.AreEqual("300 ft", Display("Train authority"));
            Assert.AreEqual("0 mph", dashboard.SelectedBlock.ActualSpeed);

            // Environmental state updates the read-only display and heater automatically.
            Assert.AreEqual("68 °F", Display("Environment temperature"));
            Assert.AreEqual("Off", Display("Track heater status"));
            _connection.Model.ApplyTemperature(new TrackModelTemperatureCommandMessage
                { BlockId = "9", TemperatureCelsius = 0 });
            Render();
            Assert.AreEqual("32 °F", Display("Environment temperature"));
            Assert.AreEqual("On", Display("Track heater status"));
            _connection.Model.ApplyFailures(new TrackModelFailureCommandMessage { BlockId = "9", PowerFailure = true });
            Render();
            Assert.AreEqual("Off", Display("Track heater status"));
        }
        finally { window.Close(); }
    }

    [TestMethod]
    public void StableStartingState_IsCapturedFromModel()
    {
        Assert.AreEqual("OCCUPIED", _vm.SelectedOutput!.Occupancy);
        Assert.AreEqual("01", _vm.SelectedOutput.Train);
        Assert.AreEqual("22 mph", _vm.SelectedOutput.ActualSpeed);
        Assert.AreEqual("NORMAL", _vm.SelectedOutput.BrokenRail);
        Assert.AreEqual("NORMAL", _vm.SelectedOutput.TrackCircuit);
        Assert.AreEqual("NORMAL", _vm.SelectedOutput.Power);
        Assert.IsTrue(_vm.TrainPresent);
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
            Assert.AreEqual(label, _vm.SelectedOutput!.TrackSignal);
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
        _vm.TrainPresent = false; await _vm.RefreshAsync();
        Assert.AreEqual("CLEAR", Output("105").Occupancy);
        Assert.AreEqual("No train", Output("105").Train);
        Assert.IsFalse(_vm.TrainPresent);
        _vm.CurrentBlock = "104"; _vm.ActualSpeed = "22"; _vm.TrainPresent = true;
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
            Assert.AreEqual(position, _vm.SelectedOutput!.State!.Switch);
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
            Assert.AreEqual(crossing, _vm.SelectedOutput!.State!.Crossing);
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
            Assert.IsTrue(_vm.TrainPresent);
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
        Assert.AreEqual("No beacon",_vm.SelectedOutput!.Beacon);
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
        Assert.AreEqual("No signal",Output("104").TrackSignal);
    }


    [TestMethod]
    public async Task CapturedSetup_UsesPrivateEndpointAndCtcContainsOnlyTickets()
    {
        await _vm.RefreshAsync();
        Assert.IsTrue(_vm.Messages.Any(m => m.Destination == "TestUI setup / feedback" && m.MessageType == nameof(TrackLayoutMessage)));
        Assert.IsTrue(_vm.Messages.Any(m => m.Destination == "TestUI setup / feedback" && m.MessageType == nameof(SystemTimeMessage)));
        Assert.IsTrue(_vm.Messages.Any(m => m.Destination == "CTC"));
        Assert.IsTrue(_vm.Messages.Where(m => m.Destination == "CTC").All(m => m.MessageType == nameof(TicketSalesMessage)));
        Assert.IsFalse(_vm.Messages.Any(m => m.Destination == "Train Controller"));
        Assert.AreEqual("12 / 8",_vm.SelectedOutput!.PassengerTotals); Assert.AreEqual("12",_vm.SelectedOutput.Tickets);
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

    private async Task UseBlueLineAsync()
    {
        _vm.Stop();
        _connection = new ModelConnection();
        BlueLineTrackLayout.LoadDemonstration(_connection.Model);
        _vm = new MainWindowViewModel(_connection);
        await _vm.RefreshAsync();
    }

    [TestMethod]
    public async Task BlueLine_ControllerCommandsAndAllCapturedDestinationsWork()
    {
        await UseBlueLineAsync();
        Assert.HasCount(15,_vm.Blocks); Assert.AreEqual("1",_vm.CurrentBlock);
        Assert.AreEqual("09:00:00",_vm.Time);
        _vm.BlockId="5"; _vm.Switch=SwitchPosition.Reverse; _vm.Speed="20"; _vm.Authority="600";
        await _vm.RefreshAsync();
        Assert.AreEqual("11",Output("5").NextBlock); Assert.AreEqual("20 mph",Output("5").Speed);
        Assert.AreEqual("600 ft",Output("5").Authority); Assert.AreEqual("0 mph",Output("1").ActualSpeed);
        _vm.BlockId="6";
        foreach(var signal in new[]{SignalState.Green,SignalState.Yellow,SignalState.Red})
        {
            _vm.Signal=signal; await _vm.RefreshAsync();
            Assert.AreEqual(signal,Output("6").Environment!.Signal);
        }
        _vm.BlockId="3"; _vm.Crossing=CrossingState.Closed; await _vm.RefreshAsync();
        Assert.AreEqual(CrossingState.Closed,Output("3").State!.Crossing);
        _vm.SelectedOutput=Output("14"); await _vm.RefreshAsync();
        Assert.AreEqual("Station C",_vm.SelectedOutput!.Beacon); Assert.AreEqual("14",_vm.SelectedOutput.Id);
        Assert.AreEqual("3",_vm.BlockId); Assert.AreEqual("1",_vm.CurrentBlock);
    }

    [TestMethod]
    public async Task BlueLine_PassengerExchangeUsesLayoutPopulationWithoutDemandInput()
    {
        await UseBlueLineAsync();
        _connection.Sent.Clear();
        Assert.AreEqual("24 waiting",Output("10").Demand); Assert.AreEqual("0",Output("10").Tickets);
        _vm.CurrentBlock="10"; _vm.Boarding="3"; _vm.Disembarking="2";
        _vm.SendTrainCommand.Execute(null); await _vm.RefreshAsync();
        Assert.AreEqual("21 waiting",Output("10").Demand); Assert.AreEqual("3 / 2",Output("10").PassengerTotals);
        Assert.AreEqual("3",Output("10").Tickets); StringAssert.Contains(_vm.TicketSales,"3 tickets/hour");
        await _vm.RefreshAsync(); Assert.AreEqual("3",Output("10").Tickets);
        Assert.AreEqual("21 waiting",Output("10").Demand);
        Assert.AreEqual("24 waiting",Output("15").Demand);
        Assert.IsFalse(_connection.Sent.Any(m => m is TrackModelPassengerDemandMessage));
    }

    [TestMethod]
    public async Task BlueLine_OrdinaryTestInputsDoNotSendSimulationSetupOrMaintenance()
    {
        await UseBlueLineAsync();
        _connection.Sent.Clear();
        _vm.Speed="20"; _vm.Authority="600"; _vm.Power=true; _vm.Time="10:00:00";
        _vm.BlockId="3"; _vm.CurrentBlock="10";
        await _vm.RefreshAsync();
        Assert.IsTrue(_connection.Sent.Any(m => m is TrackModelCommandMessage));
        Assert.IsTrue(_connection.Sent.Any(m => m is TrackModelFailureCommandMessage));
        Assert.IsTrue(_connection.Sent.Any(m => m is SystemTimeMessage));
        Assert.IsTrue(_connection.Sent.All(m => m is TrackModelCommandMessage or TrackModelTrainUpdateMessage
            or TrackModelFailureCommandMessage or SystemTimeMessage or TrackModelSnapshotRequestMessage));
    }

    [TestMethod]
    public async Task BlueLine_LayoutCaptureAndPowerFailurePreservePhysicalTrain()
    {
        await UseBlueLineAsync();
        Assert.AreEqual(50d,Output("1").Definition!.LengthMeters);
        Assert.AreEqual(50/3.6,Output("1").Definition!.SpeedLimitMetersPerSecond,1e-9);
        Assert.AreEqual("Bidirectional",Output("1").Definition!.TravelDirection);
        Assert.AreEqual("Station B",Output("9").Definition!.Beacon);
        Assert.AreEqual("No beacon",Output("10").Beacon);
        _vm.Power=true; await _vm.RefreshAsync();
        Assert.AreEqual("UNKNOWN",Output("1").Occupancy);
        Assert.AreEqual("01",Output("1").Train);
    }

    private sealed class ModelConnection : IExternalModuleConnection
    {
        public TrackService Model { get; }=new();
        public List<object> Sent { get; } = [];
        public event Action<string,MessageEnvelope>? MessageReceived;
        public event Action<string>? ErrorReported { add {} remove {} }
        public ModelConnection() => SampleTrackLayout.LoadDemo(Model);
        public Task SendAsync(object message)
        {
            Sent.Add(message);
            try
            {
                switch(message)
                {
                    case TrackModelCommandMessage c: Model.ApplyCommand(c); break;
                    case TrackModelTrainUpdateMessage t: Model.ApplyTrainUpdate(t); break;
                    case TrackModelFailureCommandMessage f: Model.ApplyFailures(f); break;
                    case SystemTimeMessage t: Model.SetSystemTime(t.SystemTime); break;
                    case TrackModelPassengerDemandMessage p: Model.ApplyPassengerDemand(p); break;
                }
                var snapshot=Guid.NewGuid();
                if(message is TrackModelSnapshotRequestMessage)
                { var layout=Model.CreateLayoutMessage(); layout.SnapshotId=snapshot; Emit(layout,"TestUI setup / feedback"); }
                foreach(var block in Model.Layout.Blocks)
                {
                    var state=Model.CreateBlockState(block.Id); state.SnapshotId=snapshot; Emit(state,"Track Controller");
                    var environment=Model.CreateTrainEnvironment(block.Id); environment.SnapshotId=snapshot; Emit(environment,"Train Model");
                }
                foreach(var line in Model.Layout.Blocks.Select(b=>b.LineId).Distinct()) Emit(Model.CreateTicketSales(line),"CTC");
                Emit(new SystemTimeMessage{SystemTime=Model.SystemTime},"TestUI setup / feedback");
                Emit(new TrackModelInputResultMessage{Accepted=true,MessageType=message.GetType().Name});
            }
            catch(ArgumentException ex) { Emit(new TrackModelInputResultMessage{Accepted=false,MessageType=message.GetType().Name,Detail=ex.Message}); }
            catch(InvalidOperationException ex) { Emit(new TrackModelInputResultMessage{Accepted=false,MessageType=message.GetType().Name,Detail=ex.Message}); }
            return Task.CompletedTask;
        }
        private void Emit(object message, string destination = "TestUI setup / feedback") => MessageReceived?.Invoke(destination,MessageSerializer.Deserialize(MessageSerializer.Serialize(message)));
    }
}
