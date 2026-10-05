using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;
namespace TrackModel.Core.Models;

public class TrackBlock
{
    public string Id { get; set; } = string.Empty;
    public string LineId { get; set; } = string.Empty;
    public string Section { get; set; } = "Main";
    public int Number { get; set; }
    public double LengthMeters { get; set; }
    public double ElevationMeters { get; set; }
    public double GradePercent { get; set; }
    public double SpeedLimitMetersPerSecond { get; set; } = 19.444444;
    public double TemperatureCelsius { get; set; } = 20;
    public string StationName { get; set; } = string.Empty;
    public int InitialWaitingPassengers { get; set; }
    public bool HasSwitch { get; set; }
    public bool HasSignal { get; set; }
    public bool HasCrossing { get; set; }
    public List<string> ConnectedBlockIds { get; set; } = [];
    public string NormalNextBlockId { get; set; } = string.Empty;
    public string ReverseNextBlockId { get; set; } = string.Empty;

    [JsonIgnore] public bool IsOccupied { get; set; }
    [JsonIgnore] public bool IsClosed { get; set; }
    [JsonIgnore] public string TrainId { get; set; } = string.Empty;
    [JsonIgnore] public double ActualSpeedMetersPerSecond { get; set; }
    [JsonIgnore] public double CommandedSpeedMetersPerSecond { get; set; }
    [JsonIgnore] public double AuthorityMeters { get; set; }
    [JsonIgnore] public SwitchPosition Switch { get; set; } = SwitchPosition.Normal;
    [JsonIgnore] public SignalState Signal { get; set; } = SignalState.Red;
    [JsonIgnore] public CrossingState Crossing { get; set; } = CrossingState.Open;
    [JsonIgnore] public bool BrokenRail { get; set; }
    [JsonIgnore] public bool TrackCircuitFailure { get; set; }
    [JsonIgnore] public bool PowerFailure { get; set; }
    [JsonIgnore] public int WaitingPassengers { get; set; }
    [JsonIgnore] public int BoardingPassengers { get; set; }
    [JsonIgnore] public int DisembarkingPassengers { get; set; }
    [JsonIgnore] public int TicketsSold { get; set; }
    [JsonIgnore] public bool HasFailure => BrokenRail || TrackCircuitFailure || PowerFailure;
    [JsonIgnore] public OccupancyState ReportedOccupancy => PowerFailure || TrackCircuitFailure
        ? OccupancyState.Unknown : IsOccupied ? OccupancyState.Occupied : OccupancyState.Clear;
    [JsonIgnore] public SignalState EffectiveSignal => !HasSignal || PowerFailure ? SignalState.Unknown : Signal;
    [JsonIgnore] public string NextBlockId => HasSwitch
        ? Switch == SwitchPosition.Reverse ? ReverseNextBlockId : NormalNextBlockId
        : ConnectedBlockIds.LastOrDefault() ?? string.Empty;
}
