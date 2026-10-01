using System.Collections.ObjectModel;
using CTC.Core.Models;

namespace CTC.TestUI.Wpf.ViewModels;

/// <summary>
/// Read-only snapshot of the real <see cref="CtcSystemState"/> owned by CTCService.
/// </summary>
/// <remarks>
/// CtcSystemState does not raise change notifications (CTC.Core stays UI-independent),
/// so the harness calls <see cref="Refresh"/> after every call it makes into CTC.Core.
/// </remarks>
public class CtcStateViewModel : ViewModelBase
{
    private readonly CtcSystemState _state;

    public CtcStateViewModel(CtcSystemState state)
    {
        _state = state;
        Refresh();
    }

    public string SystemTime => _state.SystemTime.ToString(@"hh\:mm\:ss");

    public int LineCount => _state.Lines.Count;

    public int BlockCount => _state.Lines.Sum(line => line.Blocks.Count);

    public int ScheduledTrainCount => _state.ScheduledTrains.Count;

    public int DispatchQueueCount => _state.DispatchQueue.Count;

    public int DispatchedTrainCount => _state.DispatchedTrains.Count;

    public ObservableCollection<BlockStateRow> Blocks { get; } = new ObservableCollection<BlockStateRow>();

    public void Refresh()
    {
        Blocks.Clear();

        foreach (var line in _state.Lines)
        {
            foreach (var block in line.Blocks)
            {
                Blocks.Add(new BlockStateRow(
                    line.LineId,
                    block.BlockId,
                    block.Occupancy.ToString(),
                    block.RequestedMaintenanceState.ToString(),
                    block.HasSwitch ? block.Switch.ToString() : "-",
                    block.HasSignal ? block.Signal.ToString() : "-",
                    block.HasCrossing ? block.Crossing.ToString() : "-"));
            }
        }

        OnPropertyChanged(string.Empty);
    }
}

/// <summary>One display row of CTC block state.</summary>
public sealed record BlockStateRow(
    string LineId,
    string BlockId,
    string Occupancy,
    string RequestedMaintenance,
    string Switch,
    string Signal,
    string Crossing);
