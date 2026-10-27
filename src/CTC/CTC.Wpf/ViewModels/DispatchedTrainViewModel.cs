using CTC.Core.Models;
using TrainControl.Common.Utilities;

namespace CTC.Wpf.ViewModels;

/// <summary>
/// Read-only presentation of a <see cref="DispatchedTrainState"/> for the Dispatched Trains
/// grid. Shows what CTC SUGGESTED at release, not what the Track Controller authorized.
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

    /// <summary>CTC's initial suggested speed, in mph.</summary>
    public double SuggestedSpeed => UnitConversion.MetersPerSecondToMilesPerHour(_train.SuggestedSpeedMetersPerSecond);

    /// <summary>CTC's initial suggested authority, in feet.</summary>
    public double SuggestedAuthority => UnitConversion.MetersToFeet(_train.SuggestedAuthorityMeters);

    /// <summary>Last block CTC can authoritatively place the train in; not tracked after release (see <see cref="DispatchedTrainState.LastKnownBlockId"/>).</summary>
    public string LastKnownBlock => string.IsNullOrEmpty(_train.LastKnownBlockId) ? "Unknown" : _train.LastKnownBlockId;

    /// <summary>Re-reads all values from the underlying CTC train state.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}
