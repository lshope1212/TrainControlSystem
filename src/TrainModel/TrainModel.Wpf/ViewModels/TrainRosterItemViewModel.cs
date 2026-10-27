using TrainControl.Common.Utilities;
using TrainModel.Core.Models;

namespace TrainModel.Wpf.ViewModels;

/// <summary>
/// One row in the Running Trains roster. Shows id, line, current block, and speed,
/// plus an operating-status badge. Imperial display; backend state is metric.
/// </summary>
public sealed class TrainRosterItemViewModel : ViewModelBase
{
    public TrainRosterItemViewModel(string trainId)
    {
        TrainId = trainId;
    }

    public string TrainId { get; }

    private string _lineName = string.Empty;
    public string LineName { get => _lineName; set => SetProperty(ref _lineName, value); }

    private string _positionDisplay = "--";
    public string PositionDisplay { get => _positionDisplay; set => SetProperty(ref _positionDisplay, value); }

    private string _speedDisplay = "0.0 mph";
    public string SpeedDisplay { get => _speedDisplay; set => SetProperty(ref _speedDisplay, value); }

    private string _statusDisplay = "OPERATING";
    public string StatusDisplay { get => _statusDisplay; set => SetProperty(ref _statusDisplay, value); }

    // Health kind drives the badge color in the view: "Operating" | "Faulted" | "EBraked".
    private string _statusKind = "Operating";
    public string StatusKind { get => _statusKind; set => SetProperty(ref _statusKind, value); }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    /// <summary>Update this row from a fresh train-state snapshot. The status is a LIVE
    /// health indicator: E-Braked if the emergency brake is engaged, else Faulted if any
    /// failure mode is active, else Operating.</summary>
    public void Update(TrainState s)
    {
        LineName = s.LineName;
        PositionDisplay = $"{UnitConversion.MetersToFeet(s.FrontPositionMeters):0} ft";
        SpeedDisplay = $"{UnitConversion.MetersPerSecondToMilesPerHour(s.VelocityMetersPerSecond):0.0} mph";

        bool anyFailure = s.EngineFailed || s.BrakeFailed || s.SignalPickupFailed;
        if (s.EmergencyBrakeEngaged)
        {
            StatusKind = "EBraked"; StatusDisplay = "E-BRAKED";
        }
        else if (anyFailure)
        {
            StatusKind = "Faulted"; StatusDisplay = "FAULTED";
        }
        else
        {
            StatusKind = "Operating"; StatusDisplay = "OPERATING";
        }
    }
}
