using System.Text.Json.Serialization;
using TrainControl.Contracts.Enums;

namespace TrackController.Contracts.Messages;

/// <summary>SI units throughout. Blocks describe physical topology; ownership is independent of its edges.</summary>
public sealed class WaysideConfigurationMessage
{
    public string WaysideId { get; set; } = "HW-TC01";
    public string LineName { get; set; } = "Green Line";
    public double FeedbackTimeoutSeconds { get; set; } = 5;
    public double RequestTimeoutSeconds { get; set; } = 10;
    public List<WaysideBlockDefinition> Blocks { get; set; } = [];
    /// <summary>Null preserves legacy ownership of every supplied block. Otherwise only these blocks may be commanded.</summary>
    public List<string>? OwnedBlockIds
    {
        get; set;
    }
}

/// <summary>Describes track geometry and protection zones independently of controller ownership.</summary>
public sealed class WaysideBlockDefinition
{
    public string BlockId { get; set; } = "";
    public double LengthMeters
    {
        get; set;
    }
    public double SpeedLimitMetersPerSecond
    {
        get; set;
    }
    public List<string> NextBlockIds { get; set; } = [];
    public bool HasSignal
    {
        get; set;
    }
    public string? NormalNextBlockId
    {
        get; set;
    }
    public string? ReverseNextBlockId
    {
        get; set;
    }
    public string? StemBlockId
    {
        get; set;
    }
    public List<string> SwitchProtectionBlockIds { get; set; } = [];
    public bool HasCrossing
    {
        get; set;
    }
    public List<string> CrossingProtectionBlockIds { get; set; } = [];
    public double MapX
    {
        get; set;
    }
    public double MapY
    {
        get; set;
    }
}

/// <summary>
/// Authority is measured from the specified front position along TargetBlockId + PathBlockIds.
/// Repeated status reports retain this origin; receivers must subtract traveled distance,
/// never restart the distance on receipt. TrainId is a CTC label, not inferred from occupancy.
/// </summary>
public sealed class WaysideMovementRequestMessage
{
    public string RequestId { get; set; } = "";
    public string TrainId { get; set; } = "";
    public string TargetBlockId { get; set; } = "";
    public string? EntryBlockId
    {
        get; set;
    }
    public double? FrontOffsetMeters
    {
        get; set;
    }
    public List<string> PathBlockIds { get; set; } = [];
    public double SuggestedSpeedMetersPerSecond
    {
        get; set;
    }
    public double SuggestedAuthorityMeters
    {
        get; set;
    }

    [JsonPropertyName("active")]
    public bool IsActive { get; set; } = true;
}

/// <summary>Carries a batch of measured block feedback for one validation operation.</summary>
public sealed class WaysideFeedbackMessage
{
    public List<WaysideBlockFeedback> Blocks { get; set; } = [];
}

/// <summary>Reports measured conditions and their observation time; commands are not measurements.</summary>
public sealed class WaysideBlockFeedback
{
    public string BlockId { get; set; } = "";
    public DateTimeOffset ObservedAtUtc
    {
        get; set;
    }
    public OccupancyState Occupancy { get; set; } = OccupancyState.Unknown;
    public SignalState Signal { get; set; } = SignalState.Unknown;
    public SwitchPosition Switch { get; set; } = SwitchPosition.Unknown;
    public CrossingState Crossing { get; set; } = CrossingState.Unknown;

    [JsonPropertyName("faulted")]
    public bool IsFaulted
    {
        get; set;
    }

    [JsonPropertyName("reservedByCtc")]
    public bool? IsReservedByCtc
    {
        get; set;
    }
    public double? ActualSpeedMetersPerSecond
    {
        get; set;
    }
}

/// <summary>Requests pause or speed changes without changing feedback watchdog timing.</summary>
public sealed class WaysideSimulationMessage
{
    [JsonPropertyName("paused")]
    public bool IsPaused { get; set; } = true;
    public double SpeedMultiplier { get; set; } = 1;
}

/// <summary>Requests maintenance equipment commands that remain subject to protection checks.</summary>
public sealed class WaysideManualCommandMessage
{
    public string BlockId { get; set; } = "";
    public SwitchPosition? Switch
    {
        get; set;
    }
    public SignalState? Signal
    {
        get; set;
    }
    public CrossingState? Crossing
    {
        get; set;
    }
}

/// <summary>Snapshot to CTC and Track Model. Commands never stand in for measured feedback.</summary>
public sealed record WaysideSnapshotMessage(
    string WaysideId,
    string LineName,
    DateTimeOffset EvaluatedAtUtc,
    DateTimeOffset ValidUntilUtc,
    double SimulationSeconds,
    [property: JsonPropertyName("paused")] bool IsPaused,
    double SpeedMultiplier,
    [property: JsonPropertyName("hardwareMode")] bool IsHardwareMode,
    [property: JsonPropertyName("maintenanceMode")] bool IsMaintenanceMode,
    string PlcName,
    string PlcStatus,
    IReadOnlyList<WaysideBlockStatus> Blocks,
    IReadOnlyList<WaysideAuthorization> Authorizations)
{
    /// <summary>Feedback from adjacent foreign blocks. Observations confer no command or movement authority.</summary>
    public IReadOnlyList<WaysideNeighborObservation> NeighborObservations { get; init; } = [];
}

/// <summary>Reports adjacent foreign blocks without granting command authority over them.</summary>
public sealed record WaysideNeighborObservation(
    string BlockId,
    [property: JsonPropertyName("fresh")] bool IsFresh,
    OccupancyState Occupancy,
    [property: JsonPropertyName("faulted")] bool IsFaulted,
    SignalState Signal,
    SwitchPosition Switch,
    CrossingState Crossing,
    [property: JsonPropertyName("reservedByCtc")] bool? IsReservedByCtc);
/// <summary>Combines block conditions with separate commanded and reported equipment states.</summary>
public sealed record WaysideBlockStatus(
    string BlockId,
    double LengthMeters,
    double SpeedLimitMetersPerSecond,
    IReadOnlyList<string> NextBlockIds,
    double MapX,
    double MapY,
    bool HasSwitch,
    bool HasSignal,
    bool HasCrossing,
    OccupancyState Occupancy,
    string Maintenance,
    [property: JsonPropertyName("fresh")] bool IsFresh,
    [property: JsonPropertyName("faulted")] bool IsFaulted,
    [property: JsonPropertyName("reservedByCtc")] bool? IsReservedByCtc,
    SwitchPosition CommandedSwitch,
    SwitchPosition ReportedSwitch,
    SignalState CommandedSignal,
    SignalState ReportedSignal,
    CrossingState CommandedCrossing,
    CrossingState ReportedCrossing,
    double? ActualSpeedMetersPerSecond,
    string Status);
/// <summary>Anchors permitted speed and distance to a specific request origin and route.</summary>
public sealed record WaysideAuthorization(
    string RequestId,
    string TrainId,
    string TargetBlockId,
    string? EntryBlockId,
    double FrontOffsetMeters,
    IReadOnlyList<string> PathBlockIds,
    double SuggestedSpeedMetersPerSecond,
    double SuggestedAuthorityMeters,
    double AuthorizedSpeedMetersPerSecond,
    double AuthorizedAuthorityMeters,
    string Status,
    string Reason);
/// <summary>Application result published to Track Model; transport delivery alone is not acceptance.</summary>
public sealed record WaysideAcknowledgementMessage(
    [property: JsonPropertyName("accepted")] bool IsAccepted,
    string Detail,
    string MessageType = "");
/// <summary>Requests a snapshot on the normal outbound Track Model endpoint.</summary>
public sealed class WaysideSnapshotRequest;
/// <summary>Enables maintenance controls while withholding automatic movement.</summary>
public sealed class WaysideMaintenanceModeMessage
{
    [JsonPropertyName("enabled")]
    public bool IsEnabled
    {
        get; set;
    }
}

/// <summary>Selects PLC or software logic for a local controller.</summary>
public sealed class WaysideControllerModeMessage
{
    [JsonPropertyName("hardwareMode")]
    public bool IsHardwareMode { get; set; } = true;
}

/// <summary>Carries external Boolean source for validation and loading into a paused controller.</summary>
public sealed class WaysidePlcProgramMessage
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
}
