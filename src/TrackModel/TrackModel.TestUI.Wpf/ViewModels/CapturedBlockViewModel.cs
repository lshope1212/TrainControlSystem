using System.Windows.Media;
using TrainControl.Contracts.Enums;
using TrainControl.Contracts.Messages;
namespace TrackModel.TestUI.Wpf.ViewModels;

/// <summary>Display of captured output contracts only, with no simulation logic.</summary>
public sealed class CapturedBlockViewModel(string id) : ViewModelBase
{
    public string Id { get; } = id;
    public bool? HasSignal { get; private set; }
    public bool? HasSwitch { get; private set; }
    public bool? HasCrossing { get; private set; }
    public TrackModelBlockStateMessage? State { get; private set; }
    public TrackModelTrainEnvironmentMessage? Environment { get; private set; }
    public TrackModelSignalMessage? Signal { get; private set; }
    public TrackBlockDefinition? Definition { get; private set; }
    public string Connections => Definition is null ? "—" : string.Join(", ", Definition.ConnectedBlockIds);
    public string Maintenance => State is null ? "—" : State.IsClosed ? "CLOSED" : "OPEN";
    public string StateSignal => HasSignal == false ? "N/A" : State?.Signal.ToString().ToUpperInvariant() ?? "—";
    public string Occupancy => State?.Occupancy.ToString().ToUpperInvariant() ?? "—";
    public string BrokenRail => State is null ? "—" : State.BrokenRail ? "FAILURE" : "NORMAL";
    public string TrackCircuit => State is null ? "—" : State.TrackCircuitFailure ? "FAILURE" : "NORMAL";
    public string Power => State is null ? "—" : State.PowerFailure ? "FAILURE" : "NORMAL";
    public string Switch => HasSwitch == false ? "N/A" : State?.Switch.ToString() ?? "—";
    public string Crossing => HasCrossing == false ? "N/A" : State?.Crossing.ToString() ?? "—";
    public string Speed => Environment is null ? "—" : $"{Environment.CommandedSpeedMetersPerSecond / 0.44704:0.#} mph";
    public string ActualSpeed => Environment is null ? "—" : $"{Environment.ActualSpeedMetersPerSecond / 0.44704:0.#} mph";
    public string Authority => Environment is null ? "—" : $"{Environment.AuthorityMeters / 0.3048:N0} ft";
    public string TrackSignal => HasSignal == false ? "No signal" : Environment is null ? "—" : Describe(Environment.Signal);
    public string TrafficLight => HasSignal == false ? "No signal" : Signal?.Signal.ToString().ToUpperInvariant() ?? "—";
    public string Beacon => Environment is null ? "—" : Environment.Beacon.Length == 0 ? "No beacon" : Environment.Beacon;
    public string ElevationGrade => Environment is null ? "—" : $"{Environment.ElevationMeters / 0.3048:0.#} ft / {Environment.GradePercent:0.##}%";
    public string Demand => Environment is null ? "—" : $"{Environment.WaitingPassengers} waiting";
    public string PassengerTotals => Environment is null ? "—" : $"{Environment.BoardingPassengers} / {Environment.DisembarkingPassengers}";
    public string Tickets => Environment is null ? "—" : Environment.TicketsSold.ToString("N0");
    public string Temperature => Environment is null ? "—" : $"{Environment.TemperatureCelsius * 1.8 + 32:0.#} °F";
    public string Heater => Environment is null ? "—" : !Environment.HasHeater ? "N/A" : Environment.HeaterOn ? "ON" : "OFF";
    public string Train => Environment is null ? "—" : Environment.TrainId.Length == 0 ? "No train" : Environment.TrainId;
    public string NextBlock => Environment?.NextBlockId ?? "—";
    public string LastReceived { get; private set; } = "No output received.";
    public Brush OccupancyBrush => State?.Occupancy == OccupancyState.Occupied ? Brushes.IndianRed :
        State?.Occupancy == OccupancyState.Clear ? Brushes.ForestGreen : Brushes.DarkOrange;
    public void Apply(TrackModelBlockStateMessage state) { State = state; Changed(); }
    public void Apply(TrackModelTrainEnvironmentMessage environment) { Environment = environment; Changed(); }
    public void Apply(TrackModelSignalMessage signal) { Signal = signal; Changed(); }
    public void ApplyDefinition(TrackBlockDefinition block)
    {
        HasSignal = block.HasSignal; HasSwitch = block.HasSwitch; HasCrossing = block.HasCrossing;
        Definition = block;
        OnPropertyChanged(string.Empty);
    }
    private void Changed() { LastReceived = DateTime.Now.ToString("HH:mm:ss"); OnPropertyChanged(string.Empty); }
    private static string Describe(SignalState state) => state switch
        { SignalState.Green => "PROCEED", SignalState.Yellow => "CAUTION", SignalState.Red => "STOP", _ => "UNKNOWN" };
}
