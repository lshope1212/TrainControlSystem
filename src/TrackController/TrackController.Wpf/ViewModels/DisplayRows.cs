using System.Globalization;
using TrackController.Contracts.Messages;
using TrainControl.Contracts.Enums;

namespace TrackController.Wpf.ViewModels;

/// <summary>Formats SI values for the operator display without changing controller calculations.</summary>
public static class DisplayUnits
{
    public const double MetersPerFoot = 0.3048;
    public const double MetersPerSecondPerMph = 0.44704;
    /// <summary>Converts a distance in metres to formatted feet for the operator display.</summary>
    public static string FormatFeet(double value) => (value / MetersPerFoot).ToString("N0", CultureInfo.CurrentCulture);
    /// <summary>Converts speed in metres per second to formatted miles per hour for display.</summary>
    public static string FormatMph(double value) => (value / MetersPerSecondPerMph).ToString("0.#", CultureInfo.CurrentCulture);
}

/// <summary>Presents one authorization with display units and stale-output handling.</summary>
public sealed record MovementRow(WaysideAuthorization Value, bool IsCurrent = true)
{
    public string Train => string.IsNullOrWhiteSpace(Value.TrainId) ? "â€”" : Value.TrainId;
    public string Target => Value.TargetBlockId;
    public string Path => string.Join(" â†’ ", Value.PathBlockIds);
    public string SuggestedSpeed => DisplayUnits.FormatMph(Value.SuggestedSpeedMetersPerSecond);
    public string RequestedAuthority => DisplayUnits.FormatFeet(Value.SuggestedAuthorityMeters);
    public string AuthorizedSpeed => IsCurrent ? DisplayUnits.FormatMph(Value.AuthorizedSpeedMetersPerSecond) : "â€”";
    public string AuthorizedAuthority => IsCurrent ? DisplayUnits.FormatFeet(Value.AuthorizedAuthorityMeters) : "â€”";
    public string Status => IsCurrent ? Value.Status : "Unavailable";
    public string Reason => IsCurrent ? Value.Reason : "Controller output unavailable.";
}

/// <summary>Presents commanded and measured states side by side for one equipment item.</summary>
public sealed record EquipmentRow(string Equipment, string Block, string Command, string Feedback, string Status);
/// <summary>Stores editable simulated measurements until the operator sends them through the module interface.</summary>
public sealed class FeedbackRow(string id) : ViewModelBase
{
    public string BlockId { get; } = id;

    private OccupancyState _occupancy = OccupancyState.Clear;
    private bool _faulted;
    private bool? _reserved = false;
    public OccupancyState Occupancy
    {
        get => _occupancy; set => SetProperty(ref _occupancy, value);
    }
    public bool IsFaulted
    {
        get => _faulted; set => SetProperty(ref _faulted, value);
    }
    public bool? IsReserved
    {
        get => _reserved; set => SetProperty(ref _reserved, value);
    }
    public SignalState Signal { get; set; } = SignalState.Green;
    public SwitchPosition Switch { get; set; } = SwitchPosition.Normal;
    public CrossingState Crossing { get; set; } = CrossingState.Closed;
}
