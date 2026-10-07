using CTC.Core.Models;
using TrainControl.Common.Utilities;

namespace CTC.Wpf.ViewModels;

/// <summary>
/// Read-only presentation of a <see cref="DispatchedTrainState"/> for the Dispatched Trains
/// grid. CTC stores SI units; the grid shows imperial units.
/// </summary>
public class DispatchedTrainViewModel : ViewModelBase
{
    private readonly DispatchedTrainState _train;

    public DispatchedTrainViewModel(DispatchedTrainState train)
    {
        _train = train ?? throw new ArgumentNullException(nameof(train));
    }

    public string TrainId => _train.TrainId;

    /// <summary>Track Controller-authorized speed, in mph.</summary>
    public double AuthorizedSpeed => UnitConversion.MetersPerSecondToMilesPerHour(_train.AuthorizedSpeedMetersPerSecond);

    /// <summary>Track Controller-authorized authority, in feet.</summary>
    public double Authority => UnitConversion.MetersToFeet(_train.AuthorizedAuthorityMeters);

    /// <summary>Block the train was released at; not tracked afterwards (see <see cref="DispatchedTrainState.CurrentBlockId"/>).</summary>
    public string CurrentBlock => string.IsNullOrEmpty(_train.CurrentBlockId) ? "Unknown" : _train.CurrentBlockId;

    /// <summary>Re-reads all values from the underlying CTC train state.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}
