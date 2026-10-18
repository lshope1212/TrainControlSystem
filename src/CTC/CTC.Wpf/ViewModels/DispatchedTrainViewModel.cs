using CTC.Core.Models;
using TrainControl.Common.Utilities;

namespace CTC.Wpf.ViewModels;

/// <summary>
/// Presentation of a <see cref="DispatchedTrainState"/> for the Dispatched Trains grid. Shows
/// the train's active movement plan and the LATEST speed/authority CTC suggested (at release,
/// or after the dispatcher changed the plan), not what the Track Controller authorized.
/// Read-only: the plan is changed through <see cref="MainWindowViewModel.ApplyTrainPlanCommand"/>.
/// CTC stores SI units; the grid shows imperial units.
/// </summary>
public class DispatchedTrainViewModel : ViewModelBase
{
    private readonly DispatchedTrainState _train;

    public DispatchedTrainViewModel(DispatchedTrainState train)
    {
        _train = train ?? throw new ArgumentNullException(nameof(train));
    }

    public string TrainId => _train.TrainId;

    /// <summary>Dispatcher-facing label, e.g. "Train 000".</summary>
    public string TrainDisplayName => TrainIds.DisplayName(_train.TrainId);

    /// <summary>CTC's latest suggested speed, in mph.</summary>
    public double SuggestedSpeed => UnitConversion.MetersPerSecondToMilesPerHour(_train.SuggestedSpeedMetersPerSecond);

    /// <summary>CTC's latest suggested authority, in feet.</summary>
    public double SuggestedAuthority => UnitConversion.MetersToFeet(_train.SuggestedAuthorityMeters);

    /// <summary>Block CTC infers the train is in from sequential track occupancy (see <see cref="DispatchedTrainState.CurrentBlockId"/>).</summary>
    public string CurrentBlock => string.IsNullOrEmpty(_train.CurrentBlockId) ? "Unknown" : _train.CurrentBlockId;

    /// <summary>Block the latest suggestion is planned to reach.</summary>
    public string TargetBlockId => _train.TargetBlockId;

    /// <summary>When the train is planned to enter <see cref="TargetBlockId"/>.</summary>
    public TimeSpan TargetArrivalTime => _train.TargetArrivalTime;

    /// <summary><see cref="TargetArrivalTime"/> as HH:mm:ss, matching the simulation clock.</summary>
    public string TargetArrivalTimeDisplay => _train.TargetArrivalTime.ToString(@"hh\:mm\:ss");

    /// <summary>Re-reads all values from the underlying CTC train state.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}
