using System.Collections.ObjectModel;
using System.Windows.Input;
using CTC.Core.Interfaces;
using CTC.Core.Models;
using CTC.Wpf.Commands;
using TrainControl.Contracts.Enums;

namespace CTC.Wpf.ViewModels;

/// <summary>
/// View model for the CTC office window. Translates user intent into CTC service calls;
/// it never builds outgoing contract messages itself.
/// </summary>
/// <remarks>
/// CTC state can change without any user action (messages arriving from other modules),
/// so the view model refreshes from <see cref="ICTCService.StateChanged"/>, which the
/// application always raises on the UI thread.
/// </remarks>
public class MainWindowViewModel : ViewModelBase
{
    private readonly ICTCService _ctc;
    private BlockViewModel? _selectedBlock;
    private string _communicationStatus = "No requests sent.";
    private string _inboundStatus = "No messages received.";
    private string _lastTerritoryUpdate = "--";

    public MainWindowViewModel(ICTCService ctc)
    {
        _ctc = ctc ?? throw new ArgumentNullException(nameof(ctc));

        CloseSelectedBlockCommand = new AsyncRelayCommand(_ => CloseSelectedBlockAsync(), _ => CanCloseSelectedBlock());

        //TODO other command implementations

        RebuildBlocks();
        _ctc.StateChanged += OnCtcStateChanged;
    }

    public string Title => "CTC Office";

    public string Status => "Architecture skeleton — no dispatching implemented yet.";

    public int TrainCount => _ctc.State.DispatchedTrains.Count;

    public string SystemTimeDisplay => _ctc.State.SystemTime.ToString(@"hh\:mm\:ss");

    /// <summary>Every block in the current layout, across all lines.</summary>
    public ObservableCollection<BlockViewModel> Blocks { get; } = new ObservableCollection<BlockViewModel>();

    public int OccupiedBlockCount => Blocks.Count(block => block.IsOccupied);

    /// <summary>Wall-clock time CTC state last changed.</summary>
    public string LastTerritoryUpdate
    {
        get => _lastTerritoryUpdate;
        private set => SetProperty(ref _lastTerritoryUpdate, value);
    }

    /// <summary>
    /// Block chosen by the dispatcher. Set from the block selector until the territory
    /// display renders CTC blocks.
    /// </summary>
    public BlockViewModel? SelectedBlock
    {
        get => _selectedBlock;
        set => SetProperty(ref _selectedBlock, value);
    }

    /// <summary>Result of the most recent request sent to another subsystem.</summary>
    public string CommunicationStatus
    {
        get => _communicationStatus;
        private set => SetProperty(ref _communicationStatus, value);
    }

    /// <summary>Outcome of the most recent message received from another subsystem.</summary>
    public string InboundStatus
    {
        get => _inboundStatus;
        set => SetProperty(ref _inboundStatus, value);
    }

    public ICommand CloseSelectedBlockCommand { get; }

    private bool CanCloseSelectedBlock() => SelectedBlock is not null && SelectedBlock.RequestedMaintenanceState != MaintenanceState.Closed;

    private async Task CloseSelectedBlockAsync()
    {
        var block = SelectedBlock;
        if (block is null)
        {
            return;
        }

        CommunicationStatus = $"Sending maintenance request for Block {block.BlockId}...";

        try
        {
            await _ctc.CloseBlockAsync(block.BlockId);
            CommunicationStatus = $"Maintenance request sent for Block {block.BlockId}.";
        }
        catch (Exception ex)
        {
            // MessageSendException carries a dispatcher-readable reason (e.g. "Track
            // Controller is not connected."). Everything is caught because this runs
            // from an async void command and must never crash the UI.
            CommunicationStatus = $"Unable to send maintenance request: {ex.Message}";
        }
        finally
        {
            block.Refresh();
        }
    }

    private void OnCtcStateChanged(object? sender, CtcStateChangedEventArgs e)
    {
        if (e.Kind == CtcStateChangeKind.TrackLayout)
        {
            // The old CtcBlockState objects were discarded, so the adapters must be rebuilt.
            RebuildBlocks();
        }
        else
        {
            foreach (var block in Blocks)
            {
                block.Refresh();
            }
        }

        LastTerritoryUpdate = DateTime.Now.ToString("HH:mm:ss");
        OnPropertyChanged(nameof(SystemTimeDisplay));
        OnPropertyChanged(nameof(OccupiedBlockCount));
        OnPropertyChanged(nameof(TrainCount));
        CommandManager.InvalidateRequerySuggested();
    }

    private void RebuildBlocks()
    {
        var selectedBlockId = SelectedBlock?.BlockId;

        Blocks.Clear();
        foreach (var block in _ctc.State.Lines.SelectMany(line => line.Blocks))
        {
            Blocks.Add(new BlockViewModel(block));
        }

        // Keep the dispatcher's selection when the new layout still has that block.
        SelectedBlock = Blocks.FirstOrDefault(block => block.BlockId == selectedBlockId);
    }
}
