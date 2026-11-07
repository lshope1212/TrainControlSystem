using CTC.Core.Models;
using TrainControl.Common.Utilities;
using TrainControl.Contracts.Enums;

namespace CTC.Wpf.ViewModels;

/// <summary>
/// Presentation adapter for a <see cref="CtcBlockState"/>, exposing the properties
/// MainWindow.xaml binds to via SelectedBlock.
/// </summary>
public class BlockViewModel : ViewModelBase
{
    private readonly CtcBlockState _block;

    public BlockViewModel(CtcBlockState block, string lineId = "")
    {
        _block = block ?? throw new ArgumentNullException(nameof(block));
        LineId = lineId ?? string.Empty;
    }

    /// <summary>Line the block belongs to (<see cref="CtcLineState.LineId"/>).</summary>
    public string LineId { get; }

    public string BlockId => _block.BlockId;

    public int Number => _block.BlockNumber;

    public string Section => _block.Section;

    /// <summary>
    /// Human-friendly section + block number (e.g. "A1", "W132"), independent of the
    /// internal <see cref="BlockId"/> format.
    /// </summary>
    public string DisplayName => $"{_block.Section}{_block.BlockNumber}";

    public double LengthMeters => _block.LengthMeters;

    /// <summary>Empty when the block has no station.</summary>
    public string StationName => _block.StationName;

    public bool HasStation => !string.IsNullOrWhiteSpace(_block.StationName);

    public IReadOnlyList<string> ConnectedBlockIds => _block.ConnectedBlockIds.AsReadOnly();

    public bool HasSwitch => _block.HasSwitch;

    public bool HasSignal => _block.HasSignal;

    public bool HasCrossing => _block.HasCrossing;

    public OccupancyState OccupancyState => _block.Occupancy;

    /// <summary>Latest signal aspect reported by the Track Controller.</summary>
    public SignalState Signal => _block.Signal;

    /// <summary>Latest crossing state reported by the Track Controller.</summary>
    public CrossingState Crossing => _block.Crossing;

    /// <summary>True while a maintenance request awaits Track Controller confirmation.</summary>
    public bool IsMaintenanceRequestPending => _block.RequestedMaintenanceState != _block.ConfirmedMaintenanceState;

    /// <summary>
    /// Track speed limit, in mph for display. CTC stores it in km/h as received from the layout.
    /// Read-only: the dispatcher cannot change it.
    /// </summary>
    public double SpeedLimitMilesPerHour => UnitConversion.KilometersPerHourToMilesPerHour(_block.SpeedLimitKilometersPerHour);

    /// <summary>Latest maintenance state CTC successfully requested; used for pending-request checks.</summary>
    public MaintenanceState RequestedMaintenanceState => _block.RequestedMaintenanceState;

    /// <summary>Latest maintenance state reported by the Track Controller.</summary>
    public MaintenanceState MaintenanceState => _block.ConfirmedMaintenanceState;

    public string Occupancy => _block.Occupancy.ToString();

    public bool IsOccupied => _block.Occupancy == OccupancyState.Occupied;

    // The Track Controller-reported state, not CTC's request.
    public string MaintenanceStatus => _block.ConfirmedMaintenanceState.ToString();

    /// <summary>Latest switch position reported by the Track Controller.</summary>
    public SwitchPosition SwitchPosition => _block.Switch;

    public string? SwitchStatus => _block.HasSwitch ? _block.Switch.ToString() : null;

    public string? SignalStatus => _block.HasSignal ? _block.Signal.ToDisplayName() : null;

    public string? CrossingStatus => _block.HasCrossing ? _block.Crossing.ToString() : null;

    /// <summary>Re-reads all values from the underlying CTC block state.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}
