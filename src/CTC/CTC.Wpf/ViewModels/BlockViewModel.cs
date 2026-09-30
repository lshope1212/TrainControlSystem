using CTC.Core.Models;
using TrainControl.Contracts.Enums;

namespace CTC.Wpf.ViewModels;

/// <summary>
/// Presentation adapter for a <see cref="CtcBlockState"/>, exposing the properties
/// MainWindow.xaml binds to via SelectedBlock.
/// </summary>
public class BlockViewModel : ViewModelBase
{
    private readonly CtcBlockState _block;

    public BlockViewModel(CtcBlockState block)
    {
        _block = block ?? throw new ArgumentNullException(nameof(block));
    }

    public string BlockId => _block.BlockId;

    public int Number => _block.BlockNumber;

    public bool HasSwitch => _block.HasSwitch;

    public MaintenanceState RequestedMaintenanceState => _block.RequestedMaintenanceState;

    public string Occupancy => _block.Occupancy.ToString();

    // Worded as a request: CTC has no Track Controller confirmation of maintenance state.
    public string MaintenanceStatus =>
        _block.RequestedMaintenanceState == MaintenanceState.Closed ? "Close requested" : "Open";

    public string? SwitchStatus => _block.HasSwitch ? _block.Switch.ToString() : null;

    public string? SignalStatus => _block.HasSignal ? _block.Signal.ToString() : null;

    public string? CrossingStatus => _block.HasCrossing ? _block.Crossing.ToString() : null;

    /// <summary>Re-reads all values from the underlying CTC block state.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}
