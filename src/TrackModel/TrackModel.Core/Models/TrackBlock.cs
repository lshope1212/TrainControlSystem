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
    public string Beacon { get; set; } = string.Empty;
    public string BeaconTargetBlockId { get; set; } = string.Empty;
    /// <summary>Optional surveyed distance from this beacon to the stopping point. Null means unknown.</summary>
    public double? BeaconDistanceToStopMeters { get; set; }
    public PlatformSide BeaconPlatformSide { get; set; } = PlatformSide.None;
    /// <summary>
    /// Platform side(s) of a station block (workbook "Station Side": Left, Right, Left/Right → Both).
    /// Delivered in the beacon so the train opens the correct doors. None for non-station blocks.
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public PlatformSide StationSide { get; set; } = PlatformSide.None;
    public int InitialWaitingPassengers { get; set; }
    public bool HasSwitch { get; set; }
    public bool HasSignal { get; set; }
    public bool HasCrossing { get; set; }
    public bool HasHeater { get; set; }
    public string TravelDirection { get; set; } = "Bidirectional";
    public List<string> ConnectedBlockIds { get; set; } = [];
    public string NormalNextBlockId { get; set; } = string.Empty;
    public string ReverseNextBlockId { get; set; } = string.Empty;
    /// <summary>
    /// A train leaving the yard enters the main line here (published to CTC as
    /// <c>TrackBlockDefinition.IsYardEntry</c>). Explicit layout data only: false unless the layout
    /// file says otherwise, so an imported layout never gains a yard entry it does not state.
    /// </summary>
    public bool IsYardEntry { get; set; }

    /// <summary>
    /// Physically occupied by <see cref="TrainId"/>: any part of that train is in this block. A train
    /// longer than a block, or straddling a boundary, occupies several blocks at once.
    /// </summary>
    [JsonIgnore] public bool IsOccupied { get; set; }
    [JsonIgnore] public bool IsClosed { get; set; }
    /// <summary>The train occupying the block (its owner); empty when clear. One train per block.</summary>
    [JsonIgnore] public string TrainId { get; set; } = string.Empty;
    /// <summary>
    /// The FRONT of <see cref="TrainId"/> is in this block. Exactly one block per train is its head: the
    /// block whose speed/authority code, signal and beacon the train receives. Trailing blocks stay
    /// occupied (track circuits and wayside safety) but never describe the train's environment.
    /// </summary>
    [JsonIgnore] public bool IsTrainHead { get; set; }
    [JsonIgnore] public double ActualSpeedMetersPerSecond { get; set; }
    [JsonIgnore] public double CommandedSpeedMetersPerSecond { get; set; }
    [JsonIgnore] public double AuthorityMeters { get; set; }
    /// <summary>Wayside movement request the speed/authority code belongs to; empty when none is coded.</summary>
    [JsonIgnore] public string MovementRequestId { get; set; } = string.Empty;
    /// <summary>For a continuation code: the block a train must have entered from for the code to apply.</summary>
    [JsonIgnore] public string AuthorityEntryBlockId { get; set; } = string.Empty;
    /// <summary>Where the occupying train came from (its previous block); empty when placed here or unknown.</summary>
    [JsonIgnore] public string TrainEntryBlockId { get; set; } = string.Empty;
    /// <summary>Sequence of the wayside batch that last commanded this block; older batches are ignored.</summary>
    [JsonIgnore] public long CommandSequence { get; set; }
    /// <summary>When the speed/authority code lapses to zero without a newer wayside command; null = no lease.</summary>
    [JsonIgnore] public DateTimeOffset? AuthorityExpiresAtUtc { get; set; }
    /// <summary>
    /// Whether the code is for the train in this block: always for an ordinary code, and for a
    /// continuation code only when the train entered from the code's entry block.
    /// </summary>
    [JsonIgnore] public bool CodeAppliesToOccupant => AuthorityEntryBlockId.Length == 0 || AuthorityEntryBlockId == TrainEntryBlockId;
    /// <summary>Authority the occupying train receives (an unoccupied block reports its code as is).</summary>
    [JsonIgnore] public double TrainAuthorityMeters => !IsOccupied || CodeAppliesToOccupant ? AuthorityMeters : 0;
    /// <summary>Commanded speed the occupying train receives (an unoccupied block reports its code as is).</summary>
    [JsonIgnore] public double TrainCommandedSpeedMetersPerSecond => !IsOccupied || CodeAppliesToOccupant ? CommandedSpeedMetersPerSecond : 0;
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
    [JsonIgnore] public bool HasBeacon => Beacon.Length > 0;
    // Demonstration policy: installed heaters run at/below freezing when powered.
    [JsonIgnore] public bool HeaterOn => HasHeater && TemperatureCelsius <= 0 && !PowerFailure;
    [JsonIgnore] public OccupancyState ReportedOccupancy => PowerFailure || TrackCircuitFailure
        ? OccupancyState.Unknown : IsOccupied ? OccupancyState.Occupied : OccupancyState.Clear;
    [JsonIgnore] public SignalState EffectiveSignal => !HasSignal || PowerFailure ? SignalState.Unknown : Signal;
    [JsonIgnore] public string NextBlockId => HasSwitch
        ? Switch == SwitchPosition.Reverse ? ReverseNextBlockId : NormalNextBlockId
        : ConnectedBlockIds.LastOrDefault() ?? string.Empty;
}
