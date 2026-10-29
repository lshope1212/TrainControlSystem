using CTC.Core.Models;
using CTC.Core.Scheduling;
using TrainControl.Common.Utilities;

namespace CTC.Wpf.ViewModels;

/// <summary>
/// Read-only presentation of one <see cref="MovementPlanStop"/> of a dispatched train's Active
/// Movement Plan. Edited through <see cref="MainWindowViewModel.ApplyTrainPlanCommand"/>.
/// </summary>
public class MovementPlanStopViewModel
{
    public MovementPlanStopViewModel(DispatchedTrainState train, int stopIndex)
    {
        ArgumentNullException.ThrowIfNull(train);

        var stop = train.Stops[stopIndex];
        StopIndex = stopIndex;
        BlockId = stop.BlockId;
        ArrivalTime = stop.ArrivalTime;
        DwellTime = stop.DwellTime;
        DepartureTime = stop.DepartureTime;
        PlannedSpeed = UnitConversion.MetersPerSecondToMilesPerHour(stop.PlannedSpeedMetersPerSecond);
        TimingWarning = stop.TimingWarning ?? string.Empty;
        Status = stopIndex == 0 ? DispatchedTrainViewModel.DescribePhase(train.Phase) : "Planned";
    }

    /// <summary>Position in the plan; 0 is the train's current stop.</summary>
    public int StopIndex { get; }

    /// <summary>1-based stop number shown to the dispatcher.</summary>
    public int StopNumber => StopIndex + 1;

    public string BlockId { get; }

    public TimeSpan ArrivalTime { get; }

    public TimeSpan DwellTime { get; }

    public TimeSpan DepartureTime { get; }

    public string ArrivalTimeDisplay => ArrivalTime.ToString(@"hh\:mm\:ss");

    /// <summary>Dwell in whole seconds, the form the dispatcher enters it in.</summary>
    public string DwellSecondsDisplay => DwellTimePolicy.FormatSeconds(DwellTime);

    /// <summary>Arrival plus dwell.</summary>
    public string DepartureTimeDisplay => DepartureTime.ToString(@"hh\:mm\:ss");

    /// <summary>Speed planned for the movement ending at this stop, in mph.</summary>
    public double PlannedSpeed { get; }

    public string Status { get; }

    /// <summary>Why the movement ending here cannot be made on time; empty when it can.</summary>
    public string TimingWarning { get; }
}
