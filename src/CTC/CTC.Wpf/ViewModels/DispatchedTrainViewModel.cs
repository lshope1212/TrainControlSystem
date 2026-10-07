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

    /// <summary>CTC's initial suggested speed, in mph.</summary>
    public double SuggestedSpeed => UnitConversion.MetersPerSecondToMilesPerHour(_train.SuggestedSpeedMetersPerSecond);

    /// <summary>CTC's initial suggested authority, in feet.</summary>
    public double SuggestedAuthority => UnitConversion.MetersToFeet(_train.SuggestedAuthorityMeters);

    /// <summary>Block the train was released at; not tracked afterwards (see <see cref="DispatchedTrainState.CurrentBlockId"/>).</summary>
    public string CurrentBlock => string.IsNullOrEmpty(_train.CurrentBlockId) ? "Unknown" : _train.CurrentBlockId;

    /// <summary>Re-reads all values from the underlying CTC train state.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}
